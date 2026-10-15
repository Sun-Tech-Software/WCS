using Wcs.Engine.Api;
using Wcs.Engine.Collections;
using Wcs.Engine.ORM;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;
using AutomationDevice = Wcs.Engine.Items.AutomationDevice;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Compito 15: le estrazioni per unita' di carico. In questo ciclo il motore non decide piu' quali
/// lavorazioni processare ne' quando chiuderle, e non scrive piu' <c>DISTINTA_ESTRAZIONI_UDC</c> da
/// se': riceve testate gia' scelte da <see cref="IWarehouse.GetPendingExtractionWorkAsync"/>, ne
/// risolve il movimento, e riferisce l'esito di ogni riga con
/// <see cref="IWarehouse.ReportExtractionOutcomesAsync"/>.
/// <para>
/// <b>Compito 12:</b> quella tabella non la scrive piu' nemmeno fuori da questo ciclo. La fase che
/// chiude il movimento - <c>DbInfo.UpdateStatusExtractionLu</c>, con la chiusura della lavorazione
/// attaccata - e' diventata la stessa segnalazione, e i test in fondo a questo file sono i suoi.
/// </para>
/// <para>
/// <b>Quello che questi test difendono</b> e' che gli esiti restino identici riga per riga: i
/// quattro stati espliciti, il quinto caso che nel motore era lo zero iniziale della variabile
/// <c>locked</c> (la riga gia' in estrazione, che nessun ramo tocca e che va scritta lo stesso), e i
/// due percorsi che non scrivono affatto - perche' non scrivere non e' lo stesso che scrivere zero.
/// </para>
/// <para>
/// L'ultima coppia di test difende invece qualcosa che nel motore non c'era: la coda delle scritture
/// che rifiuta il comando appena costruito, e la riga che per questo resta senza esito e torna
/// candidata al giro dopo.
/// </para>
/// </summary>
public sealed class LuExtractionOutcomesTest : IDisposable
{
    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly NavigatorStatusTypes _previousNavStatus = Navigator.NavStatus;

    /// <summary>
    /// I dispositivi messi nel diagramma, per toglierli alla fine: <c>PathDevices</c> e' statico e
    /// vive quanto il processo dei test, e ogni <see cref="AutomationDevice"/> si iscrive a
    /// <c>Navigator.StatusChanged</c> quando viene costruito.
    /// </summary>
    private readonly List<AutomationDevice> _devices = [];

    /// <summary>
    /// La lavorazione delle righe pendenti di questi test (compito 8). Non e' mai zero: il
    /// gestionale non risponde una riga la cui testata non ne ha una. Qui e' una costante perche'
    /// questi test guardano gli esiti, non la chiusura; il suo viaggio fino alla coda lo prova
    /// <see cref="TheQueuedRowKeepsTheProcessingOfTheExtractionItServes"/>.
    /// </summary>
    private const decimal Processing = 7700m;

    /// <summary>Lo scrittore di stato messo in piedi da un test, da fermare alla fine.</summary>
    private QueuedDatabaseWriter<WcsWrite>? _stateWriter;

    private readonly QueuedDatabaseWriter<WcsWrite>? _previousStateWriter = Navigator.StateWriter;

    public LuExtractionOutcomesTest()
    {
        Navigator.Store = _database;
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Navigator.ExtractionOutcomeDispatcher.Reset();
    }

    public void Dispose()
    {
        Navigator.ExtractionOutcomeDispatcher.Reset();
        // Per primo: i dispositivi che si tolgono qui sotto scrivono, e devono trovare la via di
        // prima.
        Navigator.StateWriter = _previousStateWriter;
        _stateWriter?.DisposeAsync().AsTask().GetAwaiter().GetResult();

        // Prima dello Store: togliere un dispositivo dal diagramma cancella anche la sua riga.
        foreach (var device in _devices)
        {
            PathDevices.Instance.Remove(device.ItemGuid);
            device.Dispose();
        }

        Navigator.Store = null;
        Navigator.Warehouse = _previousWarehouse;
        Navigator.NavStatus = _previousNavStatus;
        _database.Dispose();
    }

    private AutomationDevice AddDevice(Guid itemId)
    {
        var device = TestDevices.WithRow(itemId, d => d.DeviceType = DeviceTypes.CircularDistributor);
        PathDevices.Instance.Add(device);
        _devices.Add(device);
        return device;
    }

    /// <summary>
    /// Il diagramma minimo che serve a una testata: la porta di prelievo <paramref name="doorCode"/>
    /// sulla cella <paramref name="cellId"/> risolve al dispositivo sorgente, e la cella polmone
    /// <paramref name="bufferCellId"/> risolve a quello di destinazione. Entrambi nel diagramma, ne'
    /// di deposito ne' di gruppo, cosi' il costruttore del comando li usa come sono.
    /// </summary>
    private async Task<(Guid Source, Guid Destination)> BuildPlant(string doorCode, decimal cellId, decimal bufferCellId)
    {
        var sourceItem = Guid.NewGuid();
        var destinationItem = Guid.NewGuid();

        AddDevice(sourceItem);
        AddDevice(destinationItem);

        await PlantPersistence.SaveDoorMappingsAsync(sourceItem,
            [new DoorMapping { ItemId = sourceItem, DoorCode = doorCode, CellId = cellId, Side = "A" }]);
        await PlantPersistence.SaveDoorMappingsAsync(destinationItem,
            [new DoorMapping { ItemId = destinationItem, DoorCode = $"{doorCode}-BUF", CellId = bufferCellId, Side = "A" }]);

        return (sourceItem, destinationItem);
    }

    /// <summary>
    /// Uno scrittore di stato che rifiuta qualunque scrittura, perche' e' gia' fermo: e' il motore
    /// che si arresta o si riavvia mentre un giro e' in corso. Non discrimina con attese a tempo -
    /// dopo l'arresto <c>TryWrite</c> risponde <c>false</c> per sempre.
    /// </summary>
    private async Task UseAStoppedStateWriter()
    {
        var writer = new QueuedDatabaseWriter<WcsWrite>((_, _) => Task.CompletedTask);
        await writer.StopAsync();

        _stateWriter = writer;
        Navigator.StateWriter = writer;
    }

    /// <summary>
    /// <b>Il test che conta</b>: i cinque esiti che il ciclo scriveva restano gli stessi, riga per
    /// riga, e l'ultimo e' quello che si perde piu' facilmente. Nel motore la variabile nasceva
    /// <c>byte locked = 0</c>, e lo zero <b>e' anche</b> il valore di <c>InExtraction</c>: una riga
    /// che attraversava il ciclo senza incontrare nessuno dei rami espliciti - il caso normale della
    /// riga gia' in lavorazione, la cui unita' di carico e' gia' in coda proprio per quella riga -
    /// veniva scritta <c>InExtraction</c> lo stesso. Se lo spostamento scrivesse solo negli stati
    /// espliciti, quel caso smetterebbe di essere scritto e la lavorazione non si chiuderebbe piu'.
    /// </summary>
    [Fact]
    public async Task TheFiveOutcomesOfTheCycleAreReportedRowByRow()
    {
        const decimal headerId = 9100m;
        const decimal cellId = 9110m;
        const decimal bufferCellId = 9120m;

        const decimal lockedLu = 9001m;
        const decimal unreachableLu = 9002m;
        const decimal freshLu = 9003m;
        const decimal otherExtractionLu = 9004m;
        const decimal sameRowLu = 9005m;

        var (_, destinationItem) = await BuildPlant("D910", cellId, bufferCellId);

        // Le due righe gia' in coda: una per un'altra riga di estrazione, una per la propria.
        await ExtractionQueue.UpsertAsync(new ExtractionQueueItem
        {
            LoadUnitId = otherExtractionLu, SourceItemId = Guid.NewGuid(), ExtractionRowId = 8888m, Processed = false
        });
        await ExtractionQueue.UpsertAsync(new ExtractionQueueItem
        {
            LoadUnitId = sameRowLu, SourceItemId = Guid.NewGuid(), ExtractionRowId = 5m, Processed = false
        });

        var warehouse = new FakeWarehouse();

        // La bloccata non viene nemmeno cercata; la irraggiungibile non ha una posizione; le altre tre si trovano.
        foreach (var lu in new[] { freshLu, otherExtractionLu, sameRowLu })
        {
            warehouse.LoadUnitSearchResults[lu] = new LoadUnitSearchResult(lu, cellId);
            warehouse.PickUpDoorChoices[lu] = new PickUpDoorChoice(CellDoor.A, "D910", ObstacleCount: 0, []);
        }

        Navigator.Warehouse = warehouse;

        await Navigator.ServeExtractionWork(
        [
            new PendingExtraction(headerId, bufferCellId, [1m, 2m],
            [
                new PendingExtractionRow(1m, lockedLu, LoadUnitLocked: true, ProcessingId: Processing),
                new PendingExtractionRow(2m, unreachableLu, LoadUnitLocked: false, ProcessingId: Processing),
                new PendingExtractionRow(3m, freshLu, LoadUnitLocked: false, ProcessingId: Processing),
                new PendingExtractionRow(4m, otherExtractionLu, LoadUnitLocked: false, ProcessingId: Processing),
                new PendingExtractionRow(5m, sameRowLu, LoadUnitLocked: false, ProcessingId: Processing)
            ])
        ]);

        var report = Assert.Single(warehouse.ReportedExtractionOutcomes);
        Assert.Equal(headerId, report.ExtractionHeaderId);
        Assert.Equal(Navigator.WorkstationId, report.WorkstationId);

        Assert.Equal(
        [
            new ExtractionRowOutcome(1m, ExtractionRowStatus.LoadUnitLocked),
            new ExtractionRowOutcome(2m, ExtractionRowStatus.NotReachable),
            new ExtractionRowOutcome(3m, ExtractionRowStatus.InExtraction),
            new ExtractionRowOutcome(4m, ExtractionRowStatus.InOtherExtraction),
            new ExtractionRowOutcome(5m, ExtractionRowStatus.InExtraction)
        ], report.Outcomes);

        // La sola riga che diventa un comando e' quella che non era in coda: le altre due trovate
        // c'erano gia', e per la bloccata e la irraggiungibile non si costruisce niente.
        var queued = await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: freshLu, take: 10);
        var row = Assert.Single(queued);
        Assert.Equal(bufferCellId, row.BufferCellId);
        Assert.Equal(cellId, row.CellId);
        Assert.Equal(destinationItem, row.DestinationItemId);
        Assert.Equal(3m, row.ExtractionRowId);

        // La bloccata non si cerca: e' un dato che il gestionale ha gia' letto e che viaggia nella riga.
        Assert.DoesNotContain(warehouse.FoundLoadUnitIds, id => id == lockedLu);

        // L'esito della riga gia' in coda per se' e' InExtraction, ma di comando non se ne costruisce
        // un secondo: la sua riga di coda e' ancora quella di partenza, che non ha ne' cella di
        // origine ne' polmone perche' nessun costruttore l'ha toccata.
        var sameRowQueued = Assert.Single(
            await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: sameRowLu, take: 10));
        Assert.Equal(5m, sameRowQueued.ExtractionRowId);
        Assert.Null(sameRowQueued.CellId);
        Assert.Null(sameRowQueued.BufferCellId);
        Assert.Null(sameRowQueued.DestinationItemId);
    }

    /// <summary>
    /// I due percorsi che non scrivono affatto: la guida dell'impianto che non si risolve (il
    /// dispositivo non e' nel diagramma) e il punto di origine o di destinazione mancante. Nel
    /// motore facevano <c>continue</c> prima della scrittura, e vanno conservati - non scrivere non
    /// e' lo stesso che scrivere zero, perche' uno zero terrebbe aperta la lavorazione mentre
    /// l'assenza di esito la lascia chiudibile.
    /// </summary>
    [Fact]
    public async Task TheTwoPathsThatDoNotWriteReportNoOutcomeForTheirRow()
    {
        const decimal headerId = 9200m;
        const decimal cellId = 9210m;
        const decimal bufferCellId = 9220m;

        const decimal noSourceLu = 9011m;   // la porta di prelievo non e' mappata: nessun punto di origine
        const decimal noDeviceLu = 9012m;   // la porta e' mappata su un item che non e' nel diagramma
        const decimal servedLu = 9013m;     // la riga sana, per provare che il giro prosegue

        await BuildPlant("D920", cellId, bufferCellId);

        // Una porta mappata su un item che nessun dispositivo del diagramma rappresenta.
        var orphanItem = Guid.NewGuid();
        await PlantRows.EnsureAsync(orphanItem);
        await PlantPersistence.SaveDoorMappingsAsync(orphanItem,
            [new DoorMapping { ItemId = orphanItem, DoorCode = "D920-ORF", CellId = cellId, Side = "A" }]);

        var warehouse = new FakeWarehouse();

        foreach (var lu in new[] { noSourceLu, noDeviceLu, servedLu })
            warehouse.LoadUnitSearchResults[lu] = new LoadUnitSearchResult(lu, cellId);

        // "D920-IGNOTA" non e' mappata: GetItemId risponde Guid.Empty.
        warehouse.PickUpDoorChoices[noSourceLu] = new PickUpDoorChoice(CellDoor.A, "D920-IGNOTA", ObstacleCount: 0, []);
        warehouse.PickUpDoorChoices[noDeviceLu] = new PickUpDoorChoice(CellDoor.A, "D920-ORF", ObstacleCount: 0, []);
        warehouse.PickUpDoorChoices[servedLu] = new PickUpDoorChoice(CellDoor.A, "D920", ObstacleCount: 0, []);

        Navigator.Warehouse = warehouse;

        await Navigator.ServeExtractionWork(
        [
            new PendingExtraction(headerId, bufferCellId, [1m],
            [
                new PendingExtractionRow(11m, noSourceLu, LoadUnitLocked: false, ProcessingId: Processing),
                new PendingExtractionRow(12m, noDeviceLu, LoadUnitLocked: false, ProcessingId: Processing),
                new PendingExtractionRow(13m, servedLu, LoadUnitLocked: false, ProcessingId: Processing)
            ])
        ]);

        var report = Assert.Single(warehouse.ReportedExtractionOutcomes);
        Assert.Equal([new ExtractionRowOutcome(13m, ExtractionRowStatus.InExtraction)], report.Outcomes);
    }

    /// <summary>
    /// La degradazione che si conserva: il gestionale che non risponde su una riga la marca
    /// <c>NotReachable</c> - uno stato esplicito che il motore gia' modella - e <b>lascia proseguire
    /// le altre righe</b>, comprese quelle che dal gestionale non dipendono. Con l'eccezione che
    /// risale, un'indisponibilita' di pochi secondi fermerebbe l'intero giro.
    /// </summary>
    [Fact]
    public async Task AWarehouseFailureOnOneRowMarksItNotReachableAndLetsTheOthersThrough()
    {
        const decimal headerId = 9300m;
        const decimal cellId = 9310m;
        const decimal bufferCellId = 9320m;

        const decimal failingLu = 9021m;
        const decimal servedLu = 9022m;
        const decimal lockedLu = 9023m;

        await BuildPlant("D930", cellId, bufferCellId);

        var warehouse = new FakeWarehouse();
        warehouse.UnavailableOnLoadUnits.Add(failingLu);
        warehouse.LoadUnitSearchResults[servedLu] = new LoadUnitSearchResult(servedLu, cellId);
        warehouse.PickUpDoorChoices[servedLu] = new PickUpDoorChoice(CellDoor.A, "D930", ObstacleCount: 0, []);

        Navigator.Warehouse = warehouse;

        await Navigator.ServeExtractionWork(
        [
            new PendingExtraction(headerId, bufferCellId, [1m],
            [
                new PendingExtractionRow(21m, failingLu, LoadUnitLocked: false, ProcessingId: Processing),
                new PendingExtractionRow(22m, servedLu, LoadUnitLocked: false, ProcessingId: Processing),
                new PendingExtractionRow(23m, lockedLu, LoadUnitLocked: true, ProcessingId: Processing)
            ])
        ]);

        var report = Assert.Single(warehouse.ReportedExtractionOutcomes);
        Assert.Equal(
        [
            new ExtractionRowOutcome(21m, ExtractionRowStatus.NotReachable),
            new ExtractionRowOutcome(22m, ExtractionRowStatus.InExtraction),
            new ExtractionRowOutcome(23m, ExtractionRowStatus.LoadUnitLocked)
        ], report.Outcomes);

        Assert.Single(await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: servedLu, take: 10));
    }

    /// <summary>
    /// Una testata attraversata senza scrivere niente viene riferita lo stesso, con un elenco di
    /// esiti vuoto. E' la chiamata che fa chiudere una lavorazione le cui righe sono tutte gia'
    /// risolte: nel motore la chiusura girava dopo ogni testata, <b>anche quando non era stata
    /// scritta nessuna riga</b>, e senza questa chiamata quella porta si chiuderebbe.
    /// </summary>
    [Fact]
    public async Task AHeaderWhereNothingWasWrittenIsStillReportedWithAnEmptyOutcomeList()
    {
        const decimal headerId = 9400m;

        var warehouse = new FakeWarehouse();
        Navigator.Warehouse = warehouse;

        // Una sola riga, e la sua porta non e' mappata: attraversa il ciclo senza esito.
        warehouse.LoadUnitSearchResults[9031m] = new LoadUnitSearchResult(9031m, 9410m);
        warehouse.PickUpDoorChoices[9031m] = new PickUpDoorChoice(CellDoor.A, "D940-IGNOTA", ObstacleCount: 0, []);

        await Navigator.ServeExtractionWork(
        [
            new PendingExtraction(headerId, 9420m, [1m],
                [new PendingExtractionRow(31m, 9031m, LoadUnitLocked: false, ProcessingId: Processing)])
        ]);

        var report = Assert.Single(warehouse.ReportedExtractionOutcomes);
        Assert.Equal(headerId, report.ExtractionHeaderId);
        Assert.Empty(report.Outcomes);
    }

    /// <summary>
    /// Il terzo percorso che non scrive: il dispositivo di origine e' nel diagramma ma e' di
    /// deposito, e il suo ricollocatore non c'e'. Nel motore era il secondo dei due <c>continue</c>
    /// sul dispositivo nullo, quello dopo la sostituzione con <c>RelocateItemId</c>, e non era
    /// raggiungibile finche' la controfigura del diagramma costruiva solo distributori circolari.
    /// </summary>
    [Fact]
    public async Task ARowWhoseStorageDeviceHasNoRelocatorInThePlantReportsNoOutcome()
    {
        const decimal headerId = 9700m;
        const decimal cellId = 9710m;
        const decimal bufferCellId = 9720m;

        const decimal orphanRelocatorLu = 9051m;   // il suo dispositivo e' di deposito e il ricollocatore non c'e'
        const decimal servedLu = 9052m;            // la riga sana, per provare che il giro prosegue

        await BuildPlant("D970", cellId, bufferCellId);

        // Un secondo dispositivo di origine sulla stessa cella, di deposito, il cui ricollocatore
        // nessun dispositivo del diagramma rappresenta.
        var storageItem = Guid.NewGuid();
        var storage = AddDevice(storageItem);
        storage.DeviceType = DeviceTypes.Storage;
        storage.RelocateItemId = Guid.NewGuid();

        await PlantPersistence.SaveDoorMappingsAsync(storageItem,
            [new DoorMapping { ItemId = storageItem, DoorCode = "D970-STO", CellId = cellId, Side = "A" }]);

        var warehouse = new FakeWarehouse();

        foreach (var lu in new[] { orphanRelocatorLu, servedLu })
            warehouse.LoadUnitSearchResults[lu] = new LoadUnitSearchResult(lu, cellId);

        warehouse.PickUpDoorChoices[orphanRelocatorLu] = new PickUpDoorChoice(CellDoor.A, "D970-STO", ObstacleCount: 0, []);
        warehouse.PickUpDoorChoices[servedLu] = new PickUpDoorChoice(CellDoor.A, "D970", ObstacleCount: 0, []);

        Navigator.Warehouse = warehouse;

        await Navigator.ServeExtractionWork(
        [
            new PendingExtraction(headerId, bufferCellId, [1m],
            [
                new PendingExtractionRow(51m, orphanRelocatorLu, LoadUnitLocked: false, ProcessingId: Processing),
                new PendingExtractionRow(52m, servedLu, LoadUnitLocked: false, ProcessingId: Processing)
            ])
        ]);

        var report = Assert.Single(warehouse.ReportedExtractionOutcomes);
        Assert.Equal([new ExtractionRowOutcome(52m, ExtractionRowStatus.InExtraction)], report.Outcomes);

        // E niente comando per la riga muta: non scrivere e non costruire vanno insieme.
        Assert.Empty(await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: orphanRelocatorLu, take: 10));
    }

    /// <summary>
    /// Il guasto del confine sulla scrittura degli esiti di una testata non porta via le testate
    /// seguenti: prima la scrittura era diretta sul database del gestionale e il suo errore veniva
    /// inghiottito riga per riga, quindi il giro proseguiva. Con l'eccezione che risale, la prima
    /// testata che non riesce a riferire fermerebbe tutte le altre dello stesso giro.
    /// </summary>
    [Fact]
    public async Task AFailureReportingOneHeaderDoesNotTakeAwayTheFollowingHeaders()
    {
        const decimal failingHeader = 9500m;
        const decimal followingHeader = 9600m;
        const decimal cellId = 9510m;
        const decimal bufferCellId = 9520m;

        await BuildPlant("D950", cellId, bufferCellId);

        var warehouse = new FakeWarehouse();
        warehouse.UnavailableOnExtractionHeaders.Add(failingHeader);
        Navigator.Warehouse = warehouse;

        await Navigator.ServeExtractionWork(
        [
            new PendingExtraction(failingHeader, bufferCellId, [1m],
                [new PendingExtractionRow(41m, 9041m, LoadUnitLocked: true, ProcessingId: Processing)]),
            new PendingExtraction(followingHeader, bufferCellId, [1m],
                [new PendingExtractionRow(42m, 9042m, LoadUnitLocked: true, ProcessingId: Processing)])
        ]);

        var report = Assert.Single(warehouse.ReportedExtractionOutcomes);
        Assert.Equal(followingHeader, report.ExtractionHeaderId);
        Assert.Equal([new ExtractionRowOutcome(42m, ExtractionRowStatus.LoadUnitLocked)], report.Outcomes);
    }

    /// <summary>
    /// La coda delle scritture rifiuta la scrittura di una riga - lo scrittore fermo, o la coda
    /// piena: due condizioni legittime e transitorie, non difetti. Quella riga <b>non riceve
    /// esito</b>, perche' un esito la consumerebbe e non tornerebbe piu' al giro dopo; le altre
    /// righe della stessa testata proseguono, e gli esiti gia' calcolati vengono riferiti lo
    /// stesso. La riga rifiutata sta per prima apposta: se l'eccezione risalisse, si perderebbero
    /// anche gli esiti delle righe seguenti.
    /// </summary>
    [Fact]
    public async Task ARowWhoseQueuedWriteIsRejectedGetsNoOutcomeAndTheOtherRowsAreStillReported()
    {
        const decimal headerId = 9800m;
        const decimal cellId = 9810m;
        const decimal bufferCellId = 9820m;

        const decimal rejectedLu = 9061m;         // la sua scrittura viene rifiutata
        const decimal otherExtractionLu = 9062m;  // gia' in coda per un'altra riga: legge e basta
        const decimal lockedLu = 9063m;           // non scrive nemmeno: il suo esito arriva lo stesso

        await BuildPlant("D980", cellId, bufferCellId);

        await ExtractionQueue.UpsertAsync(new ExtractionQueueItem
        {
            LoadUnitId = otherExtractionLu, SourceItemId = Guid.NewGuid(), ExtractionRowId = 7777m, Processed = false
        });

        var warehouse = new FakeWarehouse();

        foreach (var lu in new[] { rejectedLu, otherExtractionLu })
        {
            warehouse.LoadUnitSearchResults[lu] = new LoadUnitSearchResult(lu, cellId);
            warehouse.PickUpDoorChoices[lu] = new PickUpDoorChoice(CellDoor.A, "D980", ObstacleCount: 0, []);
        }

        Navigator.Warehouse = warehouse;

        // Dopo l'impianto e la riga gia' in coda, che sono la scena e non il fatto sotto prova.
        await UseAStoppedStateWriter();

        await Navigator.ServeExtractionWork(
        [
            new PendingExtraction(headerId, bufferCellId, [1m],
            [
                new PendingExtractionRow(61m, rejectedLu, LoadUnitLocked: false, ProcessingId: Processing),
                new PendingExtractionRow(62m, otherExtractionLu, LoadUnitLocked: false, ProcessingId: Processing),
                new PendingExtractionRow(63m, lockedLu, LoadUnitLocked: true, ProcessingId: Processing)
            ])
        ]);

        var report = Assert.Single(warehouse.ReportedExtractionOutcomes);
        Assert.Equal(headerId, report.ExtractionHeaderId);
        Assert.Equal(
        [
            new ExtractionRowOutcome(62m, ExtractionRowStatus.InOtherExtraction),
            new ExtractionRowOutcome(63m, ExtractionRowStatus.LoadUnitLocked)
        ], report.Outcomes);

        // E la scrittura rifiutata non e' avvenuta a meta': in coda non c'e' niente per quella UDC.
        Assert.Empty(await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: rejectedLu, take: 10));
    }

    /// <summary>
    /// Il motivo per cui quella riga non riceve esito: senza esito il gestionale la ripropone, e al
    /// giro dopo - con lo scrittore di nuovo in piedi - diventa il comando che al giro prima non era
    /// riuscita a diventare. Se avesse ricevuto un esito, l'esito l'avrebbe consumata e il lavoro
    /// sarebbe andato perso in silenzio.
    /// </summary>
    [Fact]
    public async Task TheRowWhoseWriteWasRejectedIsServedAgainOnTheNextRound()
    {
        const decimal headerId = 9900m;
        const decimal cellId = 9910m;
        const decimal bufferCellId = 9920m;
        const decimal luId = 9071m;

        var (_, destinationItem) = await BuildPlant("D990", cellId, bufferCellId);

        var warehouse = new FakeWarehouse();
        warehouse.LoadUnitSearchResults[luId] = new LoadUnitSearchResult(luId, cellId);
        warehouse.PickUpDoorChoices[luId] = new PickUpDoorChoice(CellDoor.A, "D990", ObstacleCount: 0, []);
        Navigator.Warehouse = warehouse;

        PendingExtraction[] work =
        [
            new(headerId, bufferCellId, [1m],
                [new PendingExtractionRow(71m, luId, LoadUnitLocked: false, ProcessingId: Processing)])
        ];

        await UseAStoppedStateWriter();
        await Navigator.ServeExtractionWork(work);

        Assert.Empty(Assert.Single(warehouse.ReportedExtractionOutcomes).Outcomes);
        Assert.Empty(await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: luId, take: 10));

        // Il giro dopo: il motore e' ripartito, e il gestionale ripropone la stessa testata perche'
        // nessun esito ne ha chiuso la riga.
        Navigator.StateWriter = _previousStateWriter;
        await Navigator.ServeExtractionWork(work);

        Assert.Equal(2, warehouse.ReportedExtractionOutcomes.Count);
        Assert.Equal(
            [new ExtractionRowOutcome(71m, ExtractionRowStatus.InExtraction)],
            warehouse.ReportedExtractionOutcomes[1].Outcomes);

        var queued = Assert.Single(await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: luId, take: 10));
        Assert.Equal(71m, queued.ExtractionRowId);
        Assert.Equal(bufferCellId, queued.BufferCellId);
        Assert.Equal(destinationItem, queued.DestinationItemId);
    }

    /// <summary>
    /// Compito 8: la lavorazione arriva con la riga pendente e si ferma nella coda del WCS, accanto
    /// alla riga di estrazione che serve. Prima il motore la rileggeva da SQL Server alla chiusura di
    /// ogni movimento (<c>DbInfo.GetIdLavorazione</c>, un <c>JOIN</c> fra
    /// <c>RIGHE_ESTRAZIONI_UDC</c> e <c>TESTATE_ESTRAZIONI_UDC</c>): la stessa lavorazione che il
    /// gestionale aveva gia' in mano quando ha scelto la testata.
    /// <para>
    /// Le righe di ricollocamento degli ostacoli <b>non</b> la portano, come gia' non portano
    /// l'assegnazione di nota: non servono nessuna riga di estrazione, e la chiusura non passa da
    /// loro.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheQueuedRowKeepsTheProcessingOfTheExtractionItServes()
    {
        const decimal headerId = 9700m;
        const decimal cellId = 9710m;
        const decimal bufferCellId = 9720m;
        const decimal luId = 9081m;
        const decimal obstacleLu = 9082m;
        const decimal processingId = 4242m;

        await BuildPlant("D970", cellId, bufferCellId);

        var warehouse = new FakeWarehouse();
        warehouse.LoadUnitSearchResults[luId] = new LoadUnitSearchResult(luId, cellId);
        warehouse.PickUpDoorChoices[luId] =
            new PickUpDoorChoice(CellDoor.A, "D970", ObstacleCount: 1, [obstacleLu]);
        Navigator.Warehouse = warehouse;

        await Navigator.ServeExtractionWork(
        [
            new PendingExtraction(headerId, bufferCellId, [1m],
                [new PendingExtractionRow(81m, luId, LoadUnitLocked: false, ProcessingId: processingId)])
        ]);

        var queued = Assert.Single(await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: luId, take: 10));
        Assert.Equal(81m, queued.ExtractionRowId);
        Assert.Equal(processingId, queued.ProcessingId);

        var obstacle = Assert.Single(
            await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: obstacleLu, take: 10));
        Assert.False(obstacle.Extract);
        Assert.Equal(0m, obstacle.ProcessingId);
    }

    // ------------------------------------------------------------------------------------------
    // Compito 12: l'altra fase, quella che chiude il movimento.
    //
    // I test qui sopra guardano il giro che COSTRUISCE la coda: e' la prima pronuncia su una riga
    // di estrazione. Quelli qui sotto guardano la SECONDA - servita, non raggiungibile, gia' in
    // un'altra estrazione - che fino al compito 11 il motore scriveva da se' su
    // DISTINTA_ESTRAZIONI_UDC con DbInfo.UpdateStatusExtractionLu, chiudendo poi la lavorazione per
    // conto suo. Adesso e' lo stesso fatto, la stessa operazione del confine, e la chiusura la trae
    // il gestionale (compito 11).
    // ------------------------------------------------------------------------------------------

    /// <summary>Mette in coda una riga che serve la riga di estrazione data, e ne torna l'identificativo.</summary>
    private static async Task<decimal> QueueRowServing(decimal loadUnitId, decimal? extractionRowId)
    {
        await ExtractionQueue.UpsertAsync(new ExtractionQueueItem
        {
            LoadUnitId = loadUnitId,
            SourceItemId = Guid.NewGuid(),
            ExtractionRowId = extractionRowId,
            Processed = false
        });

        var queued = Assert.Single(
            await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: loadUnitId, take: 10));

        return queued.Id;
    }

    /// <summary>
    /// <b>Il test che conta del compito 12</b>: l'esito della seconda fase e' una segnalazione, con la
    /// riga di estrazione letta dalla coda del WCS, la postazione del WCS e <b>la testata a zero</b> -
    /// chi arriva qui conosce la riga e non la testata, e il gestionale ricava da se' la lavorazione
    /// dalle righe segnalate (<c>LavorazioniDelleRigheSql</c>, compito 11).
    /// <para>
    /// L'ultima asserzione e' quella che il brief chiama «nessuna scrittura locale sostituisce il
    /// fatto»: la riga di coda non viene toccata. Prima di questo compito qui partiva un
    /// <c>UPDATE DISTINTA_ESTRAZIONI_UDC</c> sul database del gestionale, seguito da una lettura del
    /// residuo e da una chiusura; non c'e' piu' niente di tutto questo, e nemmeno un suo surrogato di
    /// qua.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(ExtractionRowStatus.Extracted)]
    [InlineData(ExtractionRowStatus.NotReachable)]
    [InlineData(ExtractionRowStatus.InOtherExtraction)]
    public async Task TheOutcomeOfTheClosingPhaseIsReportedWithTheRowReadFromTheQueue(ExtractionRowStatus status)
    {
        const decimal luId = 9301m;
        const decimal extractionRowId = 931m;

        var queueRowId = await QueueRowServing(luId, extractionRowId);

        var warehouse = new FakeWarehouse();
        Navigator.Warehouse = warehouse;

        await DbInfo.ReportExtractionRowOutcome(queueRowId, status);

        var report = Assert.Single(warehouse.ReportedExtractionOutcomes);
        Assert.Equal(0m, report.ExtractionHeaderId);
        Assert.Equal(Navigator.WorkstationId, report.WorkstationId);
        Assert.Equal([new ExtractionRowOutcome(extractionRowId, status)], report.Outcomes);

        var queued = Assert.Single(await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: luId, take: 10));
        Assert.Equal(extractionRowId, queued.ExtractionRowId);
        Assert.False(queued.Processed);
    }

    /// <summary>
    /// Le due guardie che restano di qua, come nell'originale: una riga di coda che non esiste e una
    /// riga di coda <b>senza</b> riga di estrazione non hanno niente da segnalare. La seconda e' il
    /// caso normale delle righe nate da un'assegnazione di nota o da un ricollocamento, che sulla
    /// distinta del gestionale non hanno una riga propria - l'<c>UPDATE</c> di prima partiva anche lui
    /// solo con <c>ExtractionRowId &gt; 0</c>. Il confine non viene nemmeno toccato: e' quello che
    /// prova <c>Calls</c> vuota.
    /// </summary>
    [Fact]
    public async Task AQueueRowWithoutAnExtractionRowReportsNothing()
    {
        var queueRowId = await QueueRowServing(9302m, extractionRowId: null);

        var warehouse = new FakeWarehouse();
        Navigator.Warehouse = warehouse;

        await DbInfo.ReportExtractionRowOutcome(0m, ExtractionRowStatus.NotReachable);
        await DbInfo.ReportExtractionRowOutcome(queueRowId, ExtractionRowStatus.NotReachable);
        await DbInfo.ReportExtractionRowOutcome(queueRowId + 10_000m, ExtractionRowStatus.NotReachable);

        Assert.Empty(warehouse.ReportedExtractionOutcomes);
        Assert.Empty(warehouse.Calls);
    }

    /// <summary>
    /// La regola del guasto del confine: un gestionale che non risponde <b>solleva</b>, e non diventa
    /// un esito perso in silenzio. Qui non vale la degradazione riga per riga che
    /// <c>Navigator.ServeExtraction</c> si puo' permettere - la' e' una riga fra tante di una testata,
    /// qui e' l'unico momento in cui questa riga viene pronunciata - e un esito che sparisse
    /// lascerebbe la lavorazione aperta per sempre. Ogni chiamante ha il proprio <c>try/catch</c> o il
    /// proprio ciclo che ripassa.
    /// </summary>
    [Fact]
    public async Task AWarehouseFailureReportingTheOutcomeSurfacesInsteadOfBeingSwallowed()
    {
        var queueRowId = await QueueRowServing(9303m, extractionRowId: 933m);

        Navigator.Warehouse = new FakeWarehouse { Unavailable = true };

        var ex = await Assert.ThrowsAsync<WarehouseUnavailableException>(
            () => DbInfo.ReportExtractionRowOutcome(queueRowId, ExtractionRowStatus.Extracted));

        Assert.Equal(nameof(IWarehouse.ReportExtractionOutcomesAsync), ex.Operation);
    }

    /// <summary>
    /// Il punto che porta l'esito nuovo del contratto: la missione arrivata alla postazione segnala
    /// la sua riga come <see cref="ExtractionRowStatus.Extracted"/>
    /// (<c>PathMission.ProcessExtractionItems</c>). E' l'esito finale dell'estrazione riuscita, ed e'
    /// da qui che il gestionale trae la chiusura della lavorazione: era
    /// <c>StatusExtractionLu.Extracted</c> scritto dal motore, e per questo
    /// <see cref="ExtractionRowStatus"/> non lo aveva.
    /// <para>
    /// La riga di coda non porta assegnazioni, cosi' le due chiamate che le seguono nello stesso
    /// metodo escono sulle loro guardie e questo test resta sul proprio ramo. Che la riga di coda
    /// sparisca dopo e' il comportamento di sempre, e l'asserzione lo fissa: la segnalazione viene
    /// prima, e un guasto del confine la lascerebbe in coda per il giro dopo.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheMissionArrivedAtTheStationReportsItsRowAsExtracted()
    {
        const decimal luId = 9304m;
        const decimal extractionRowId = 934m;

        var queueRowId = await QueueRowServing(luId, extractionRowId);

        var warehouse = new FakeWarehouse();
        Navigator.Warehouse = warehouse;

        var mission = new Items.PathMission(Guid.NewGuid()) { LuId = luId, ExtractionQueueId = queueRowId };

        await mission.ProcessExtractionItems();

        var report = Assert.Single(warehouse.ReportedExtractionOutcomes);
        Assert.Equal([new ExtractionRowOutcome(extractionRowId, ExtractionRowStatus.Extracted)], report.Outcomes);

        Assert.Empty(await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: luId, take: 10));
        Assert.Empty(await ExtractionOutcomeOutbox.PendingAsync(100));
    }

    /// <summary>
    /// Il punto del magazzino: una riga di estrazione la cui unita' di carico ha gia' una missione
    /// viva per un'altra riga esce dalla coda, e la sua riga di estrazione viene segnalata
    /// <see cref="ExtractionRowStatus.InOtherExtraction"/>
    /// (<c>AutomationDeviceStorageManager.ProcessExistingMission</c>).
    /// </summary>
    [Fact]
    public async Task ARowWhoseLoadUnitIsAlreadyOnAnotherMissionIsReportedInOtherExtraction()
    {
        const decimal luId = 9305m;
        const decimal extractionRowId = 935m;

        var queueRowId = await QueueRowServing(luId, extractionRowId);

        var warehouse = new FakeWarehouse();
        Navigator.Warehouse = warehouse;

        var table = new System.Data.DataTable();
        table.Columns.Add("ID_ESTRAZIONE_UDC", typeof(decimal));
        table.Columns.Add("ID_ASSEGNAZIONE_UDC", typeof(decimal));
        table.Columns.Add("GUID_ASSEGNAZIONE_UDC", typeof(Guid));
        table.Columns.Add("ID_UDC", typeof(decimal));
        table.Rows.Add(queueRowId, 0m, Guid.Empty, 0m);

        // La missione viva non serve nessuna riga di coda (zero) e non porta un'assegnazione: e' il
        // ramo finale del metodo, quello che segnala e toglie la riga dalla coda.
        var existing = new Items.PathMission(Guid.NewGuid()) { LuId = luId };

        Assert.False(await Items.Managers.AutomationDeviceStorageManager.ProcessExistingMission(table.Rows[0], existing));

        var report = Assert.Single(warehouse.ReportedExtractionOutcomes);
        Assert.Equal([new ExtractionRowOutcome(extractionRowId, ExtractionRowStatus.InOtherExtraction)], report.Outcomes);

        Assert.Empty(await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: luId, take: 10));
    }

    /// <summary>
    /// Il punto del prelievo: una missione che il dispositivo non riesce piu' a prelevare - il
    /// gestionale non trova piu' l'unita' di carico dove il ricollocatore la puo' prendere - esce
    /// dalla coda del dispositivo, e la sua riga di estrazione viene segnalata
    /// <see cref="ExtractionRowStatus.NotReachable"/>
    /// (<c>AutomationDeviceLuManager.ValidateAllMissionsPickupable</c>, dentro <c>CanPickUpLUs</c>).
    /// </summary>
    [Fact]
    public async Task AMissionThatCanNoLongerBePickedUpIsReportedNotReachable()
    {
        const decimal luId = 9306m;
        const decimal extractionRowId = 936m;

        var queueRowId = await QueueRowServing(luId, extractionRowId);

        var warehouse = new FakeWarehouse();
        Navigator.Warehouse = warehouse;

        var device = AddDevice(Guid.NewGuid());

        // Profondita' non nulla: con zero la verifica si ferma prima, su un'altra regola.
        var mission = new Items.PathMission(Guid.NewGuid())
        {
            LuId = luId, ExtractionQueueId = queueRowId, StorageDepth = 1
        };

        // Il gestionale non sa dove sia questa unita' di carico: VerifyPickUp risponde no.
        Assert.False(await device.LuManager!.CanPickUpLUs([mission]));

        var report = Assert.Single(warehouse.ReportedExtractionOutcomes);
        Assert.Equal([new ExtractionRowOutcome(extractionRowId, ExtractionRowStatus.NotReachable)], report.Outcomes);
    }

    /// <summary>
    /// Lo stesso punto, guardato sul guasto del confine: la segnalazione viene <b>prima</b> della
    /// mutazione locale, quindi un gestionale che non risponde lascia la missione dov'era, nella coda
    /// di prelievo del dispositivo.
    /// <para>
    /// Fino alla revisione del compito 12 <c>RemoveLuToPickUp</c> stava per prima: su un guasto la
    /// missione era gia' uscita dalla coda del dispositivo ma restava viva in
    /// <c>ActiveMissions</c>, al giro dopo non era piu' nell'elenco che si valida, e
    /// <c>ProcessExistingMission</c> usciva sulla sua prima guardia senza segnalare. Quell'esito non
    /// si sarebbe segnalato mai piu'. Prima del compito 12 il percorso non esisteva, perche' la
    /// scrittura di allora non poteva sollevare.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AWarehouseFailureOnThePickupOutcomeLeavesTheMissionInTheDeviceQueue()
    {
        const decimal luId = 9307m;
        const decimal extractionRowId = 937m;

        var queueRowId = await QueueRowServing(luId, extractionRowId);

        // Solo la segnalazione dell'esito e' guasta: FindLoadUnitAsync deve poter rispondere «non la
        // trovo», che e' il no di VerifyPickUp, altrimenti il test proverebbe un'altra strada.
        var warehouse = new FakeWarehouse();
        warehouse.UnavailableOperations.Add(nameof(IWarehouse.ReportExtractionOutcomesAsync));
        Navigator.Warehouse = warehouse;

        var device = AddDevice(Guid.NewGuid());

        var mission = new Items.PathMission(Guid.NewGuid())
        {
            LuId = luId, ExtractionQueueId = queueRowId, StorageDepth = 1
        };

        device.EnqueueLuToPickUp(mission);

        var ex = await Assert.ThrowsAsync<WarehouseUnavailableException>(
            () => device.LuManager!.CanPickUpLUs([mission]));

        Assert.Equal(nameof(IWarehouse.ReportExtractionOutcomesAsync), ex.Operation);

        // La coda di prelievo non e' stata toccata: la missione e' ancora li' per il giro dopo.
        Assert.Same(mission, Assert.Single(device.QueuedLUtoPickUp));
    }
}
