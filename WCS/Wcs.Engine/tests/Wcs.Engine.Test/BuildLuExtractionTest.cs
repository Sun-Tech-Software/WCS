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
/// Compito 14: <see cref="Navigator.BuildLuExtraction"/> non decide più quali note assegnare né
/// perché scartarle - le righe arrivano già assegnate e già validate da
/// <see cref="IWarehouse.GetPendingNoteWorkAsync"/> (compito 14, lato gestionale in
/// <c>MagazzinoPerWcs.ValidaAssegnazioniPronteAsync</c>, provato a parte in
/// <c>WMS.Services.Test.MagazzinoPerWcsTest</c>). Restano qui due cose che il gestionale non può
/// fare al posto del motore: non riproporre un'assegnazione già in coda (prima lo faceva
/// <c>DbInfo.GetNoteAssignmentsToQueue</c> leggendo la coda del WCS, che il gestionale non vede), e
/// cercare la UDC nel vivo del diagramma per costruire il comando di estrazione.
/// </summary>
public sealed class BuildLuExtractionTest : IDisposable
{
    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly NavigatorStatusTypes _previousNavStatus = Navigator.NavStatus;
    private readonly bool _previousLimit = Navigator.LimitOneShippingNotePerBuffer;

    /// <summary>
    /// I dispositivi che questi test mettono nel diagramma, per toglierli alla fine: <c>PathDevices</c>
    /// e' statico e vive quanto il processo dei test, e ogni <see cref="AutomationDevice"/> si iscrive
    /// a <c>Navigator.StatusChanged</c> quando viene costruito - lasciarli dentro semina dispositivi
    /// iscritti agli eventi negli altri test della suite.
    /// </summary>
    private readonly List<AutomationDevice> _devices = [];

    public BuildLuExtractionTest()
    {
        Navigator.Store = _database;
    }

    public void Dispose()
    {
        // Prima dello Store: togliere un dispositivo dal diagramma cancella anche la sua riga.
        foreach (var device in _devices)
        {
            PathDevices.Instance.Remove(device.ItemGuid);
            device.Dispose();
        }

        Navigator.NavStatus = _previousNavStatus;
        Navigator.LimitOneShippingNotePerBuffer = _previousLimit;
        Navigator.Store = null;
        Navigator.Warehouse = _previousWarehouse;
        _database.Dispose();
    }

    /// <summary>Mette un dispositivo nel diagramma e lo segna, cosi' il <see cref="Dispose"/> lo toglie.</summary>
    private AutomationDevice AddDevice(Guid itemId)
    {
        var device = TestDevices.WithRow(itemId, d => d.DeviceType = DeviceTypes.CircularDistributor);
        PathDevices.Instance.Add(device);
        _devices.Add(device);
        return device;
    }

    /// <summary>
    /// Decisione 2: <c>LimitOneShippingNotePerBuffer</c> resta un'opzione della configurazione
    /// dell'host del WCS, ma chi la applica e' il gestionale - quindi deve arrivargli, e con il
    /// valore che ha davvero. Prima del compito 14 la leggeva il ciclo di assegnazione del motore;
    /// fra il compito 14 e questa decisione non la leggeva piu' nessuno, e un impianto che l'avesse
    /// accesa perdeva il comportamento in silenzio. Chi fa la domanda e' <c>ExtractionProcess</c>,
    /// che legge il lavoro pronto prima di prendere il semaforo dell'estrazione.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThePlantOptionTravelsToTheWarehouseWithTheValueItHas(bool limit)
    {
        Navigator.LimitOneShippingNotePerBuffer = limit;

        var warehouse = new FakeWarehouse();
        Navigator.Warehouse = warehouse;

        await Navigator.ExtractionProcess();

        Assert.Equal([limit], warehouse.PendingNoteWorkLimits);
    }

    /// <summary>
    /// <b>Il test che conta, lato motore</b>: una riga pronta che supera anche la ricerca nel vivo
    /// (cella trovata, porta senza ostacoli, sorgente e destinazione nel diagramma) finisce in coda
    /// con gli stessi campi che <c>Navigator.BuildLuExtraction</c> scriveva leggendo
    /// <c>ASSEGNAZIONE_NOTE</c> direttamente - solo che ora vengono da <see cref="PendingNote"/>.
    /// </summary>
    [Fact]
    public async Task AReadyRowThatIsFoundInTheWarehouseIsEnqueuedWithTheFieldsFromThePendingNote()
    {
        const decimal loadUnitId = 8001m;
        const decimal cellId = 8100m;
        const decimal bufferCellId = 8200m;
        const decimal assignmentId = 501m;
        var assignmentGuid = Guid.NewGuid();
        var sourceItem = Guid.NewGuid();
        var destinationItem = Guid.NewGuid();

        // Il dispositivo sorgente (dietro la porta A123) e quello di destinazione: nel diagramma, ne'
        // di deposito ne' di gruppo, cosi' BuildSingleMoveLu li usa cosi' come sono.
        AddDevice(sourceItem);
        AddDevice(destinationItem);

        await PlantPersistence.SaveDoorMappingsAsync(sourceItem,
            [new DoorMapping { ItemId = sourceItem, DoorCode = "A123", CellId = cellId, Side = "A" }]);

        var warehouse = new FakeWarehouse();
        warehouse.LoadUnitSearchResults[loadUnitId] = new LoadUnitSearchResult(loadUnitId, cellId);
        warehouse.PickUpDoorChoices[loadUnitId] = new PickUpDoorChoice(CellDoor.A, "A123", ObstacleCount: 0, []);
        Navigator.Warehouse = warehouse;

        PendingNote[] noteWork =
        [
            new PendingNote(assignmentId, assignmentGuid, loadUnitId, bufferCellId, [1m, 2m], destinationItem, "WS1",
                ProcessingId: 5010m)
        ];

        await Navigator.BuildLuExtraction(noteWork);

        var rows = await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: null, take: 10);
        var row = Assert.Single(rows);
        Assert.Equal(loadUnitId, row.LoadUnitId);
        Assert.Equal(bufferCellId, row.BufferCellId);
        Assert.Equal(cellId, row.CellId);
        Assert.Equal(sourceItem, row.SourceItemId);
        Assert.Equal(destinationItem, row.DestinationItemId);
        Assert.Equal("WS1", row.WorkstationId);
        Assert.Equal(assignmentId, row.AssignmentId);
        Assert.Equal(assignmentGuid, row.AssignmentGuid);

        // Compito 9: e la lavorazione della nota, che viaggia con l'assegnazione e si ferma qui - il
        // motore non la rilegge piu' dal gestionale a ogni giro di ogni ricollocatore. Il campo della
        // lavorazione di estrazione (compito 8) resta a zero: questa riga non serve nessuna riga di
        // estrazione, e le due lavorazioni stanno su tabelle diverse.
        Assert.Equal(5010m, row.NoteProcessingId);
        Assert.Equal(0m, row.ProcessingId);

        Assert.Contains(warehouse.Calls, c => c == nameof(IWarehouse.FindLoadUnitAsync));
        Assert.Contains(warehouse.Calls, c => c == nameof(IWarehouse.ChoosePickUpDoorAsync));
    }

    /// <summary>
    /// <b>Il test che conta della decisione 3</b>: il quarto motivo di rifiuto - la UDC non si trova
    /// nelle aree servite dal polmone - lo scopre il motore, che e' l'unico a saperla cercare nel
    /// vivo, ma la conseguenza la esegue il gestionale. L'esito e' lo stesso di prima
    /// (l'assegnazione sparisce, cosi' il gestionale puo' assegnarne un'altra) e la riga non entra
    /// in coda; quello che cambia e' chi cancella: non piu' <c>Navigator.RigettaAssegnazione</c> con
    /// <c>DbInfo.DeleteAssignedLuByAssignmentId</c>, ma un'operazione del confine che porta anche il
    /// motivo.
    /// </summary>
    [Fact]
    public async Task ALoadUnitThatIsNotFoundInTheBufferAreasIsRejectedThroughTheWarehouseWithItsReason()
    {
        const decimal loadUnitId = 8004m;
        const decimal assignmentId = 504m;

        // Nessuna riga in LoadUnitSearchResults: la ricerca non la trova.
        var warehouse = new FakeWarehouse();
        Navigator.Warehouse = warehouse;

        await Navigator.BuildLuExtraction(
            [new PendingNote(assignmentId, Guid.NewGuid(), loadUnitId, 8204m, [1m], Guid.NewGuid(), "WS1", 5040m)]);

        var rifiuto = Assert.Single(warehouse.RejectedNoteAssignments);
        Assert.Equal(assignmentId, rifiuto.AssignmentId);
        Assert.Equal(loadUnitId, rifiuto.LoadUnitId);
        Assert.Equal("la UDC non e' disponibile nelle aree servite dal polmone", rifiuto.Reason);

        Assert.Empty(await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: loadUnitId, take: 10));
    }

    /// <summary>
    /// Il punto a cui era dedicata <c>DbInfo.GetNoteAssignmentsToQueue</c> prima di compito 14: una
    /// riga già in coda (stesso <c>AssignmentId</c>) non va ricercata né riaccodata, anche se il
    /// gestionale continua a riproporla - il gestionale non vede la coda del WCS, quindi tocca al
    /// motore non ripeterla. Senza questo filtro l'assegnazione verrebbe accodata di nuovo a ogni
    /// giro, con una riga duplicata a ogni ciclo di <c>MasterProcess</c>.
    /// </summary>
    [Fact]
    public async Task AnAssignmentAlreadyQueuedIsNotSearchedOrEnqueuedAgain()
    {
        const decimal loadUnitId = 8002m;
        const decimal assignmentId = 777m;

        await ExtractionQueue.UpsertAsync(new ExtractionQueueItem
        {
            LoadUnitId = loadUnitId, SourceItemId = Guid.NewGuid(), AssignmentId = assignmentId, Processed = false
        });

        var warehouse = new FakeWarehouse();
        Navigator.Warehouse = warehouse;

        await Navigator.BuildLuExtraction(
            [new PendingNote(assignmentId, Guid.NewGuid(), loadUnitId, 8201m, [1m], Guid.NewGuid(), "WS1", 7770m)]);

        var rows = await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: loadUnitId, take: 10);
        Assert.Single(rows);
        Assert.DoesNotContain(warehouse.Calls, c => c == nameof(IWarehouse.FindLoadUnitAsync));
    }

    /// <summary>
    /// La pulizia delle righe delle note sospese (<c>DbInfo.DeleteLuInExtranctionByPausedNote</c>)
    /// non sta nel giro dell'estrazione: un suo errore non deve portarsi via la costruzione della
    /// coda dello stesso giro, che dipende da tutt'altro. Girava in testa al ciclo di assegnazione,
    /// e per un tratto e' stata dentro <c>ExtractionProcess</c>, dove un suo errore lasciava il giro
    /// senza <see cref="Navigator.BuildLuExtraction"/> - oltre a tenere il semaforo dell'estrazione
    /// mentre chiedeva al gestionale il codice di ogni UDC cancellata.
    /// </summary>
    /// <remarks>
    /// Il modo di far fallire la pulizia: una riga in coda con un'assegnazione le fa superare
    /// l'uscita anticipata sulla coda vuota e la porta alla domanda al confine, che qui non e'
    /// servita. Fino al compito 6 il guasto era di un'altra natura - la pulizia leggeva le
    /// lavorazioni delle note su <c>Navigator.Database</c>, la connessione a SQL Server, nulla in
    /// prova - e adesso quella lettura passa dal gestionale come tutte le altre: a farla fallire
    /// e' <c>UnavailableOperations</c> sulla sola <c>GetPausedNoteAssignmentsAsync</c>, cosi' il
    /// resto del giro dell'estrazione e' servito normalmente.
    /// </remarks>
    [Fact]
    public async Task TheExtractionCycleStillBuildsTheQueueWhenThePausedNoteCleanupWouldThrow()
    {
        const decimal loadUnitId = 8003m;
        const decimal cellId = 8102m;
        const decimal bufferCellId = 8203m;
        const decimal assignmentId = 503m;
        var sourceItem = Guid.NewGuid();
        var destinationItem = Guid.NewGuid();

        // La riga che manda la pulizia fino alla domanda al confine: un'assegnazione diversa da
        // quella pronta, altrimenti la riga pronta verrebbe saltata come gia' in coda.
        await ExtractionQueue.UpsertAsync(new ExtractionQueueItem
        {
            LoadUnitId = 9001m, SourceItemId = Guid.NewGuid(), AssignmentId = 999m, Processed = false
        });

        AddDevice(sourceItem);
        AddDevice(destinationItem);

        await PlantPersistence.SaveDoorMappingsAsync(sourceItem,
            [new DoorMapping { ItemId = sourceItem, DoorCode = "A124", CellId = cellId, Side = "A" }]);

        var warehouse = new FakeWarehouse();
        warehouse.LoadUnitSearchResults[loadUnitId] = new LoadUnitSearchResult(loadUnitId, cellId);
        warehouse.PickUpDoorChoices[loadUnitId] = new PickUpDoorChoice(CellDoor.A, "A124", ObstacleCount: 0, []);
        warehouse.PendingNoteWork =
        [
            new PendingNote(assignmentId, Guid.NewGuid(), loadUnitId, bufferCellId, [1m], destinationItem, "WS1", 5030m)
        ];

        // Il guasto e' solo quello della pulizia: se fosse dentro il giro dell'estrazione, la coda
        // non verrebbe costruita.
        warehouse.UnavailableOperations.Add(nameof(IWarehouse.GetPausedNoteAssignmentsAsync));

        Navigator.Warehouse = warehouse;
        Navigator.NavStatus = NavigatorStatusTypes.Run;

        await Navigator.ExtractionProcess();

        var rows = await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: loadUnitId, take: 10);
        var row = Assert.Single(rows);
        Assert.Equal(assignmentId, row.AssignmentId);
    }

    /// <summary>
    /// La gemella della guardia qui sopra, per la lettura del lavoro pronto: la domanda al
    /// gestionale sta <b>prima</b> della presa del semaforo dell'estrazione. Costa una richiesta al
    /// gestionale piu' due richiami del gestionale al WCS per ogni assegnazione candidata, e sotto
    /// il lucchetto quella spesa la pagavano gli altri sette punti del motore che se lo contendono
    /// con trenta secondi di attesa.
    /// </summary>
    /// <remarks>
    /// Il semaforo e' preso dal test e non viene mai rilasciato prima della chiamata, e a
    /// discriminare i due casi non e' un tetto di secondi ma una corsa fra due eventi:
    /// <see cref="FakeWarehouse.PendingNoteWorkAsked"/>, che il magazzino finto fa scattare quando
    /// il giro gli chiede il lavoro pronto, e il ritorno del giro stesso. Con la lettura fuori dal
    /// lucchetto scatta il segnale; se tornasse dentro non scatterebbe mai, e a completarsi sarebbe
    /// il giro, che esce a mani vuote quando scade il suo tetto d'attesa sul semaforo. Un tetto di
    /// secondi, invece, sotto carico scade anche su codice sano: il modo di fallire sarebbe un rosso
    /// falso. Non serve alcuna sincronizzazione con altri test: la suite ha la parallelizzazione
    /// spenta (<c>Impostazioni.cs</c>).
    /// </remarks>
    [Fact]
    public async Task ThePendingNoteWorkIsAskedForBeforeTakingTheExtractionLock()
    {
        // Nessuna riga pronta: cosi' il giro esce subito dopo la lettura, senza nemmeno provare a
        // prendere il semaforo che il test tiene.
        var warehouse = new FakeWarehouse();
        Navigator.Warehouse = warehouse;
        Navigator.NavStatus = NavigatorStatusTypes.Run;

        await Navigator.SyncExtraction.WaitAsync();

        Task round;
        bool askedBeforeTheLock;

        try
        {
            round = Navigator.ExtractionProcess();

            // Si guarda lo stato del segnale, non chi ha vinto la corsa: quando la lettura sta
            // fuori dal lucchetto il segnale scatta comunque prima che il giro possa tornare, e
            // fra i due completamenti WhenAny puo' riportare l'uno o l'altro.
            await Task.WhenAny(warehouse.PendingNoteWorkAsked, round);
            askedBeforeTheLock = warehouse.PendingNoteWorkAsked.IsCompleted;
        }
        finally
        {
            Navigator.SyncExtraction.Release();
        }

        // Prima delle verifiche, e anche quando il test sta per fallire: un giro rimasto appeso
        // prenderebbe il semaforo appena rilasciato e lo terrebbe fuori da questo test, facendo
        // scadere l'attesa degli altri.
        await round;

        Assert.True(askedBeforeTheLock,
            "ExtractionProcess non ha chiesto il lavoro pronto con il semaforo dell'estrazione in mano ad altri: la lettura sta dentro il lucchetto");

        Assert.Contains(warehouse.Calls, c => c == nameof(IWarehouse.GetPendingNoteWorkAsync));
    }

    /// <summary>
    /// La guardia del compito 16. Il punto e' la riga senza item di destinazione: prima chiedeva la
    /// porta A al gestionale con <c>CelleMagazzino.DecodificaCella</c>, che rispondeva un risultato
    /// <b>annullabile</b> - una cella polmone che non c'e' (zero) tornava nulla, il chiamante
    /// scriveva <c>?.CODICE_PORTA_A</c> e la riga finiva semplicemente senza destinazione.
    /// <see cref="IWarehouse.DescribeCellAsync"/> risponde invece un valore non annullabile e tratta
    /// l'assenza come guasto: senza una guardia esplicita sullo zero la stessa riga solleverebbe.
    /// <para>
    /// <b>Perche' la prova non e' «non solleva».</b> <see cref="Navigator.BuildLuExtraction"/>
    /// avvolge tutto il ciclo in un <c>try/catch</c> che scrive nel registro e torna: un'eccezione
    /// non uscirebbe mai dalla chiamata, e un test che si limitasse a chiamarla sarebbe verde con la
    /// guardia e senza. Quello che l'eccezione porta via davvero e' il <b>resto del giro</b> -
    /// il <c>catch</c> sta fuori dal <c>foreach</c>. Quindi si guardano due cose che senza guardia
    /// cambiano tutt'e due: che al gestionale la cella zero non venga nemmeno chiesta, e che la
    /// riga buona che segue finisca comunque in coda.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ARowWithoutDestinationAndWithoutBufferCellIsSkippedWithoutAskingTheWarehouseOrLosingTheRestOfTheRound()
    {
        const decimal skippedLoadUnitId = 8005m;
        const decimal skippedCellId = 8105m;
        const decimal loadUnitId = 8006m;
        const decimal cellId = 8106m;
        const decimal bufferCellId = 8206m;
        const decimal assignmentId = 506m;
        var sourceItem = Guid.NewGuid();
        var destinationItem = Guid.NewGuid();

        AddDevice(sourceItem);
        AddDevice(destinationItem);

        await PlantPersistence.SaveDoorMappingsAsync(sourceItem,
            [new DoorMapping { ItemId = sourceItem, DoorCode = "A125", CellId = cellId, Side = "A" }]);

        var warehouse = new FakeWarehouse();
        warehouse.LoadUnitSearchResults[skippedLoadUnitId] = new LoadUnitSearchResult(skippedLoadUnitId, skippedCellId);
        warehouse.PickUpDoorChoices[skippedLoadUnitId] = new PickUpDoorChoice(CellDoor.A, "A125", ObstacleCount: 0, []);
        warehouse.LoadUnitSearchResults[loadUnitId] = new LoadUnitSearchResult(loadUnitId, cellId);
        warehouse.PickUpDoorChoices[loadUnitId] = new PickUpDoorChoice(CellDoor.A, "A125", ObstacleCount: 0, []);
        Navigator.Warehouse = warehouse;

        // La prima riga e' quella che tocca la guardia: nessun item di destinazione, e nessuna cella
        // polmone da cui ricavarlo. La seconda e' una riga buona, che deve arrivare in coda lo
        // stesso.
        await Navigator.BuildLuExtraction(
        [
            new PendingNote(505m, Guid.NewGuid(), skippedLoadUnitId, 0m, [1m], null, "WS1", 5050m),
            new PendingNote(assignmentId, Guid.NewGuid(), loadUnitId, bufferCellId, [1m], destinationItem, "WS1", 5060m)
        ]);

        Assert.DoesNotContain(warehouse.Calls, c => c == nameof(IWarehouse.DescribeCellAsync));

        var row = Assert.Single(await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: null, take: 10));
        Assert.Equal(assignmentId, row.AssignmentId);
        Assert.Equal(loadUnitId, row.LoadUnitId);
    }
}
