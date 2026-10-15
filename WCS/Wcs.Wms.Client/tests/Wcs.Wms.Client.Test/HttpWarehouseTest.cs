using System.Net;
using System.Text.Json;
using Wcs.Wms.Contracts;
using Xunit;

namespace Wcs.Wms.Client.Test;

public class HttpWarehouseTest
{
    private static HttpWarehouse Build(HttpStatusCode code, string body = "{}")
    {
        var handler = new StubHandler(code, body);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        return new HttpWarehouse(http, new HttpWarehouseOptions { ApiKey = "prova" });
    }

    [Fact]
    public async Task ATimeoutBecomesWarehouseUnavailable()
    {
        var http = new HttpClient(new ThrowingHandler(new TaskCanceledException()))
            { BaseAddress = new Uri("http://localhost/") };
        var warehouse = new HttpWarehouse(http, new HttpWarehouseOptions { ApiKey = "prova" });

        var ex = await Assert.ThrowsAsync<WarehouseUnavailableException>(
            () => warehouse.DescribeCellAsync(42));

        Assert.Equal("DescribeCell", ex.Operation);
    }

    [Fact]
    public async Task AServerErrorBecomesWarehouseUnavailable()
    {
        var warehouse = Build(HttpStatusCode.InternalServerError);

        await Assert.ThrowsAsync<WarehouseUnavailableException>(() => warehouse.DescribeCellAsync(42));
    }

    [Fact]
    public async Task ANotFoundOnALocationIsAnAnswer_NotAFailure()
    {
        var warehouse = Build(HttpStatusCode.NotFound);

        Assert.Null(await warehouse.LocateLoadUnitAsync(4711));
    }

    [Fact]
    public async Task AMalformedJsonBodyBecomesWarehouseUnavailable()
    {
        var warehouse = Build(HttpStatusCode.OK, "questo non e' json");

        var ex = await Assert.ThrowsAsync<WarehouseUnavailableException>(
            () => warehouse.DescribeCellAsync(42));

        Assert.Equal("DescribeCell", ex.Operation);
    }

    [Fact]
    public async Task CallerCancellationDoesNotBecomeWarehouseUnavailable()
    {
        var http = new HttpClient(new CancelingHandler()) { BaseAddress = new Uri("http://localhost/") };
        var warehouse = new HttpWarehouse(http, new HttpWarehouseOptions { ApiKey = "prova" });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => warehouse.DescribeCellAsync(42, cts.Token));
    }

    [Fact]
    public async Task CallerCancellationDoesNotBecomeWarehouseUnavailable_OnAPostToo()
    {
        // Gli stessi quattro metodi privati condividono lo schema del catch: questo test copre la
        // famiglia dei Post, non solo GetAsync, così un ordine dei catch rotto non passa inosservato.
        var http = new HttpClient(new CancelingHandler()) { BaseAddress = new Uri("http://localhost/") };
        var warehouse = new HttpWarehouse(http, new HttpWarehouseOptions { ApiKey = "prova" });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => warehouse.ReportMissionLeftAssignmentUnservedAsync(
                new MissionLeftAssignmentUnservedRequest(Guid.NewGuid(), 1, Guid.NewGuid(), 0, "prova"), cts.Token));
    }

    [Fact]
    public async Task ANullBodyOnAnOkResponseBecomesWarehouseUnavailable()
    {
        // Un 200 con corpo che deserializza a null non è «non c'è» (quello è il 404): è un guasto,
        // perché il gestionale si comporta in un modo che non capiamo.
        var warehouse = Build(HttpStatusCode.OK, "null");

        var ex = await Assert.ThrowsAsync<WarehouseUnavailableException>(
            () => warehouse.LocateLoadUnitAsync(4711));

        Assert.Equal("LocateLoadUnit", ex.Operation);
    }

    [Fact]
    public async Task TheApiKeyTravelsInTheHeader()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var warehouse = new HttpWarehouse(http, new HttpWarehouseOptions { ApiKey = "prova" });

        await warehouse.DescribeCellAsync(42);

        Assert.Equal("prova", handler.LastRequest!.Headers.GetValues(WarehouseRoutes.ApiKeyHeader).Single());
    }

    /// <summary>
    /// Decisione 2: l'opzione d'impianto <c>LimitOneShippingNotePerBuffer</c> resta nella
    /// configurazione dell'host del WCS e viaggia nella richiesta del lavoro pronto. Se non
    /// arrivasse al gestionale, il limite tornerebbe a essere una voce di configurazione che
    /// nessuno legge.
    /// </summary>
    [Theory]
    [InlineData(false, "limitOneShippingNotePerBuffer=false")]
    [InlineData(true, "limitOneShippingNotePerBuffer=true")]
    public async Task ThePlantOptionTravelsInTheQueryOfThePendingNoteWork(bool limite, string atteso)
    {
        var handler = new StubHandler(HttpStatusCode.OK, "[]");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var warehouse = new HttpWarehouse(http, new HttpWarehouseOptions { ApiKey = "prova" });

        await warehouse.GetPendingNoteWorkAsync(limite);

        Assert.Contains(atteso, handler.LastRequest!.RequestUri!.Query);
    }

    /// <summary>
    /// Compito 9: la lavorazione della nota viaggia nell'assegnazione pendente. Il motore la
    /// rileggeva da sé a ogni giro di ogni ricollocatore (<c>DbInfo.ProcessingIdsByAssignment</c>,
    /// una <c>SELECT</c> con la lista letterale delle assegnazioni in coda); ora arriva insieme
    /// all'assegnazione e resta nella coda del WCS. Se il campo non attraversasse il confine, la
    /// missione nascerebbe con zero e la lavorazione della nota non si chiuderebbe più, in
    /// silenzio - lo stesso guasto della gemella del compito 8.
    /// </summary>
    [Fact]
    public async Task ThePendingNoteCarriesItsProcessing()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """
            [{"assignmentId":601,"assignmentGuid":null,"loadUnitId":8601,"bufferCellId":8620,
              "bufferAreaIds":[1],"destinationItemId":null,"workstationId":"WS1","processingId":7010}]
            """);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var warehouse = new HttpWarehouse(http, new HttpWarehouseOptions { ApiKey = "prova" });

        var assegnazione = Assert.Single(await warehouse.GetPendingNoteWorkAsync(limitOneShippingNotePerBuffer: false));

        Assert.Equal(7010m, assegnazione.ProcessingId);
    }

    /// <summary>
    /// Compito 15: le due operazioni delle estrazioni per unità di carico sono una lettura e una
    /// scrittura, e viaggiano come tali - GET la prima, POST la seconda, come le altre operazioni
    /// che scrivono. Un elenco vuoto è una risposta buona per la lettura: «niente pronto» non è un
    /// guasto.
    /// </summary>
    [Fact]
    public async Task ThePendingExtractionWorkIsAGetAndAnEmptyListIsAnAnswer()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "[]");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var warehouse = new HttpWarehouse(http, new HttpWarehouseOptions { ApiKey = "prova" });

        Assert.Empty(await warehouse.GetPendingExtractionWorkAsync());

        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.EndsWith($"{WarehouseRoutes.Prefix}/{WarehouseRoutes.PendingExtractionWork}",
            handler.LastRequest.RequestUri!.AbsolutePath);
    }

    /// <summary>
    /// Compito 8: la lavorazione dell'estrazione viaggia nella riga pendente. Il motore la rileggeva
    /// da SQL Server a chiusura di ogni movimento (<c>DbInfo.GetIdLavorazione</c>, una <c>SELECT</c>
    /// su <c>TESTATE_ESTRAZIONI_UDC</c>); ora arriva insieme alla riga e resta nella coda del WCS.
    /// Se il campo non attraversasse il confine, la riga nascerebbe con zero e la lavorazione non si
    /// chiuderebbe piu', in silenzio: e' la stessa forma di guasto che il gestionale si guarda dal
    /// lato suo.
    /// </summary>
    [Fact]
    public async Task ThePendingExtractionRowCarriesItsProcessing()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """
            [{"extractionHeaderId":700,"pickingCellId":500,"bufferAreaIds":[9],
              "rows":[{"extractionRowId":11,"loadUnitId":4001,"loadUnitLocked":false,"processingId":77}]}]
            """);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var warehouse = new HttpWarehouse(http, new HttpWarehouseOptions { ApiKey = "prova" });

        var testata = Assert.Single(await warehouse.GetPendingExtractionWorkAsync());

        Assert.Equal(new PendingExtractionRow(11m, 4001m, LoadUnitLocked: false, ProcessingId: 77m),
            Assert.Single(testata.Rows));
    }

    /// <summary>
    /// Compito 20: la ricerca della referenza e' un POST con i due soli campi che il motore sa - la
    /// referenza e il polmone - e la risposta e' <b>sempre</b> un corpo, mai un 404: i due modi di
    /// non trovare niente sono esiti nel corpo, e il motore ne lascia all'operatore due messaggi
    /// diversi. E' la differenza da <c>FindLoadUnit</c>, che invece traduce il 404 in null.
    /// </summary>
    [Fact]
    public async Task TheReferenceSearchIsAPostCarryingTheReferenceAndTheBufferAndItsOutcomeIsInTheBody()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"outcome":1,"loadUnitId":0,"cellId":0}""");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var warehouse = new HttpWarehouse(http, new HttpWarehouseOptions { ApiKey = "prova" });

        var esito = await warehouse.FindLoadUnitByReferenceAsync(new FindLoadUnitByReferenceRequest("REF-1", 500m));

        Assert.Equal(new ReferenceSearchResult(ReferenceSearchOutcome.NoPickingAreas, 0, 0), esito);

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.EndsWith($"{WarehouseRoutes.Prefix}/{WarehouseRoutes.FindLoadUnitByReference}",
            handler.LastRequest.RequestUri!.AbsolutePath);

        // I nomi sono in maiuscola iniziale, non in minuscola come nel corpo di
        // ReportExtractionOutcomes qui sotto: le due POST di HttpWarehouse serializzano con
        // impostazioni diverse (JsonSerializer.Serialize quella che aspetta una risposta,
        // JsonContent.Create quella che non l'aspetta). Il gestionale legge in tutt'e due i casi -
        // la deserializzazione di ASP.NET Core non distingue le maiuscole - e lo stesso vale per
        // ReserveStorage e FindLoadUnit, che passano di li' da prima di questo lavoro.
        var corpo = handler.LastRequestBody!;
        Assert.Contains("\"ReferenceId\":\"REF-1\"", corpo);
        Assert.Contains("\"BufferCellId\":500", corpo);
    }

    /// <summary>
    /// Un guasto del gestionale sulla ricerca della referenza resta un guasto: non c'e' nessun esito
    /// che significhi «non ho potuto chiedere», e il motore lascia il comando in errore.
    /// </summary>
    [Fact]
    public async Task AServerErrorOnTheReferenceSearchBecomesWarehouseUnavailable()
    {
        var warehouse = Build(HttpStatusCode.InternalServerError);

        var ex = await Assert.ThrowsAsync<WarehouseUnavailableException>(
            () => warehouse.FindLoadUnitByReferenceAsync(new FindLoadUnitByReferenceRequest("REF-1", 500m)));

        Assert.Equal("FindLoadUnitByReference", ex.Operation);
    }

    [Fact]
    public async Task TheExtractionOutcomesTravelInThePostBody()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var warehouse = new HttpWarehouse(http, new HttpWarehouseOptions { ApiKey = "prova" });

        await warehouse.ReportExtractionOutcomesAsync(new ReportExtractionOutcomesRequest(
            42m, "KazamControl", [new ExtractionRowOutcome(7m, ExtractionRowStatus.InOtherExtraction)]));

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.EndsWith($"{WarehouseRoutes.Prefix}/{WarehouseRoutes.ExtractionOutcomes}",
            handler.LastRequest.RequestUri!.AbsolutePath);

        var corpo = handler.LastRequestBody!;
        Assert.Contains("\"extractionHeaderId\":42", corpo);
        Assert.Contains("\"status\":3", corpo);
    }

    /// <summary>
    /// Un guasto del gestionale sulla scrittura degli esiti resta un guasto del confine: è il motore
    /// a decidere che farne (vedi <c>Navigator.ServeExtractionWork</c>, che lo registra e passa alla
    /// testata seguente), non il client a inghiottirlo.
    /// </summary>
    [Fact]
    public async Task AServerErrorReportingTheExtractionOutcomesBecomesWarehouseUnavailable()
    {
        var warehouse = Build(HttpStatusCode.InternalServerError);

        var ex = await Assert.ThrowsAsync<WarehouseUnavailableException>(
            () => warehouse.ReportExtractionOutcomesAsync(new ReportExtractionOutcomesRequest(42m, "KazamControl", [])));

        Assert.Equal("ReportExtractionOutcomes", ex.Operation);
    }

    /// <summary>
    /// Compito 17: leggere la prenotazione di una missione e' una lettura che ammette l'assenza,
    /// come <see cref="IWarehouse.LocateLoadUnitAsync"/>. Un 404 e' la risposta «questa missione non
    /// ha una prenotazione», non un guasto: e' quello che fa riprenotare il motore invece di
    /// lasciare la missione in attesa di un gestionale che sta benissimo.
    /// </summary>
    [Fact]
    public async Task ANotFoundOnAStorageReservationIsAnAnswer_NotAFailure()
    {
        var warehouse = Build(HttpStatusCode.NotFound);

        Assert.Null(await warehouse.GetStorageReservationAsync(Guid.NewGuid()));
    }

    /// <summary>
    /// Le due operazioni nuove viaggiano per quello che sono: la lettura come GET sul percorso della
    /// missione, il rilascio come POST, con la missione nel corpo, come tutte le altre operazioni di
    /// questo confine che scrivono.
    /// </summary>
    [Fact]
    public async Task TheStorageReservationIsReadWithAGetOnTheMissionPath()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"missionId":"11111111-1111-1111-1111-111111111111","outcome":0,"cellId":7,"doorCode":"A7","partition":"0002","storageDepth":150,"needsCompaction":false}""");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var warehouse = new HttpWarehouse(http, new HttpWarehouseOptions { ApiKey = "prova" });
        var missione = Guid.Parse("11111111-1111-1111-1111-111111111111");

        var prenotazione = await warehouse.GetStorageReservationAsync(missione);

        Assert.Equal(7, prenotazione!.CellId);
        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.EndsWith($"{WarehouseRoutes.Prefix}/reservations/{missione}", handler.LastRequest.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task TheStorageReservationIsReleasedWithAPost()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var warehouse = new HttpWarehouse(http, new HttpWarehouseOptions { ApiKey = "prova" });
        var missione = Guid.NewGuid();

        await warehouse.ReleaseStorageReservationAsync(missione);

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.EndsWith($"{WarehouseRoutes.Prefix}/{WarehouseRoutes.ReleaseReservation}",
            handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Contains(missione.ToString(), handler.LastRequestBody!);
    }

    /// <summary>
    /// Compito 18: le tre operazioni nuove viaggiano per quello che sono. La lettura delle posizioni
    /// di una cella e' un GET sul percorso della cella, e la sua rotta e' <b>diversa</b> da quella
    /// del contenuto: sono due letture diverse, non la stessa con un'opzione.
    /// </summary>
    [Fact]
    public async Task TheCellPositionsAreReadWithAGetOnTheirOwnPath()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """[{"loadUnitId":11,"reserved":false},{"loadUnitId":12,"reserved":true}]""");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var warehouse = new HttpWarehouse(http, new HttpWarehouseOptions { ApiKey = "prova" });

        var positions = await warehouse.GetCellPositionsAsync(42);

        Assert.Equal([false, true], positions.Select(p => p.Reserved));
        Assert.Equal([11m, 12m], positions.Select(p => p.LoadUnitId));
        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.EndsWith($"{WarehouseRoutes.Prefix}/cells/42/positions", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.NotEqual(WarehouseRoutes.CellContent, WarehouseRoutes.CellPositions);
    }

    [Fact]
    public async Task TheCellIsClearedWithAPostCarryingTheCell()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var warehouse = new HttpWarehouse(http, new HttpWarehouseOptions { ApiKey = "prova" });

        await warehouse.ClearCellPositionsAsync(4242);

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.EndsWith($"{WarehouseRoutes.Prefix}/{WarehouseRoutes.ClearCellPositions}",
            handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Contains("4242", handler.LastRequestBody!);
    }

    /// <summary>
    /// La regola del guasto vale anche sulle tre operazioni nuove: la lettura delle posizioni non
    /// ammette l'assenza (una cella vuota e' una lista vuota, non un 404), quindi un 404 e' un
    /// guasto, non una risposta.
    /// </summary>
    [Fact]
    public async Task ANotFoundOnTheCellPositionsIsAFailure_NotAnAnswer()
    {
        var warehouse = Build(HttpStatusCode.NotFound);

        var ex = await Assert.ThrowsAsync<WarehouseUnavailableException>(
            () => warehouse.GetCellPositionsAsync(42));

        Assert.Equal("GetCellPositions", ex.Operation);
    }

    /// <summary>
    /// La segnalazione viaggia con un POST che porta l'unita' di carico <b>e</b> l'assegnazione:
    /// erano due chiamate su due rotte diverse, ed e' un fatto solo. La rotta e' sua, non quella del
    /// rifiuto dell'assegnazione: quella cancella un'assegnazione, questa racconta che un'unita' di
    /// carico ha lasciato la coda.
    /// </summary>
    [Fact]
    public async Task TheLoadUnitLeavingTheExtractionQueueIsOneSignalCarryingBothIdentifiers()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var warehouse = new HttpWarehouse(http, new HttpWarehouseOptions { ApiKey = "prova" });

        await warehouse.ReportLoadUnitLeftExtractionQueueAsync(new LoadUnitLeftExtractionQueueRequest(4711, 777));

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.EndsWith($"{WarehouseRoutes.Prefix}/{WarehouseRoutes.LoadUnitLeftExtractionQueue}",
            handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Contains("4711", handler.LastRequestBody!);
        Assert.Contains("777", handler.LastRequestBody!);
        Assert.NotEqual(WarehouseRoutes.RejectNoteAssignment, WarehouseRoutes.LoadUnitLeftExtractionQueue);
        Assert.NotEqual(WarehouseRoutes.MissionLeftAssignmentUnserved, WarehouseRoutes.LoadUnitLeftExtractionQueue);
    }

    /// <summary>
    /// La regola del guasto vale anche qui: se il gestionale non riceve la segnalazione, la cosa si
    /// dice. Un 500 diventa <see cref="WarehouseUnavailableException"/> con il nome dell'operazione.
    /// </summary>
    [Fact]
    public async Task AServerErrorOnTheLoadUnitLeftSignalBecomesWarehouseUnavailable()
    {
        var warehouse = Build(HttpStatusCode.InternalServerError);

        var ex = await Assert.ThrowsAsync<WarehouseUnavailableException>(
            () => warehouse.ReportLoadUnitLeftExtractionQueueAsync(new LoadUnitLeftExtractionQueueRequest(4711, 777)));

        Assert.Equal("ReportLoadUnitLeftExtractionQueue", ex.Operation);
    }

    /// <summary>
    /// Compito 21: i tre fatti che hanno preso il posto di <c>ReleaseAssignment</c> viaggiano su tre
    /// rotte distinte, e ognuna porta la propria chiave. La prova le confronta a due a due: sono
    /// operazioni diverse proprio perche' chiave, forma o semantica non coincidono, e una rotta
    /// condivisa lo smentirebbe.
    /// </summary>
    [Fact]
    public async Task TheThreeAssignmentFactsTravelOnThreeDistinctRoutes()
    {
        var missionId = Guid.NewGuid();
        var assignmentGuid = Guid.NewGuid();

        var (unserved, unservedBody) = await Posted(w => w.ReportMissionLeftAssignmentUnservedAsync(
            new MissionLeftAssignmentUnservedRequest(missionId, 4711, assignmentGuid, 0, "missione tolta dalle attive")));
        Assert.EndsWith($"{WarehouseRoutes.Prefix}/{WarehouseRoutes.MissionLeftAssignmentUnserved}", unserved);
        Assert.Contains("4711", unservedBody);
        Assert.Contains(assignmentGuid.ToString(), unservedBody);
        Assert.Contains("missione tolta dalle attive", unservedBody);

        var (served, servedBody) = await Posted(w => w.ReportMissionServedAssignmentAsync(
            new MissionServedAssignmentRequest(missionId, 4712, assignmentGuid, "arrivata a destinazione")));
        Assert.EndsWith($"{WarehouseRoutes.Prefix}/{WarehouseRoutes.MissionServedAssignment}", served);
        Assert.Contains("4712", servedBody);

        var (unservable, unservableBody) = await Posted(w => w.ReportUnservableQueuedExtractionAsync(
            new UnservableQueuedExtractionRequest(900, 4713, assignmentGuid, "impegnata altrove")));
        Assert.EndsWith($"{WarehouseRoutes.Prefix}/{WarehouseRoutes.UnservableQueuedExtraction}", unservable);
        Assert.Contains("900", unservableBody);

        Assert.Equal(3, new HashSet<string> { unserved, served, unservable }.Count);
    }

    /// <summary>
    /// La regola del guasto vale per tutt'e tre: un 500 diventa
    /// <see cref="WarehouseUnavailableException"/> con il nome dell'operazione, non un silenzio.
    /// </summary>
    [Theory]
    [InlineData("ReportMissionLeftAssignmentUnserved")]
    [InlineData("ReportMissionServedAssignment")]
    [InlineData("ReportUnservableQueuedExtraction")]
    public async Task AServerErrorOnAnAssignmentFactBecomesWarehouseUnavailable(string operazione)
    {
        var warehouse = Build(HttpStatusCode.InternalServerError);

        var ex = await Assert.ThrowsAsync<WarehouseUnavailableException>(() => operazione switch
        {
            "ReportMissionLeftAssignmentUnserved" => warehouse.ReportMissionLeftAssignmentUnservedAsync(
                new MissionLeftAssignmentUnservedRequest(Guid.NewGuid(), 4711, Guid.NewGuid(), 0, "prova")),
            "ReportMissionServedAssignment" => warehouse.ReportMissionServedAssignmentAsync(
                new MissionServedAssignmentRequest(Guid.NewGuid(), 4711, Guid.NewGuid(), "prova")),
            _ => warehouse.ReportUnservableQueuedExtractionAsync(
                new UnservableQueuedExtractionRequest(900, 4711, Guid.NewGuid(), "prova"))
        });

        Assert.Equal(operazione, ex.Operation);
    }

    /// <summary>Il percorso e il corpo di una POST andata a buon fine.</summary>
    private static async Task<(string Path, string Body)> Posted(Func<HttpWarehouse, Task> post)
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };

        await post(new HttpWarehouse(http, new HttpWarehouseOptions { ApiKey = "prova" }));

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        return (handler.LastRequest.RequestUri!.AbsolutePath, handler.LastRequestBody!);
    }

    /// <summary>
    /// Compito 19, terza operazione: la conferma delle assegnazioni servite viaggia con
    /// l'assegnazione e l'utente, e su una rotta sua. Non e' quella dell'assegnazione servita dalla
    /// missione - che pure parla di un'assegnazione arrivata - ne' quella della nota che torna non
    /// assegnata: chiavi e semantiche diverse, rotte diverse.
    /// </summary>
    [Fact]
    public async Task TheServedAssignmentsAreConfirmedWithAPostCarryingTheAssignmentAndTheUser()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var warehouse = new HttpWarehouse(http, new HttpWarehouseOptions { ApiKey = "prova" });

        await warehouse.ConfirmServedAssignmentsAsync(new ConfirmServedAssignmentsRequest(900, "KazamControl.Process"));

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.EndsWith($"{WarehouseRoutes.Prefix}/{WarehouseRoutes.ConfirmServedAssignments}",
            handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Contains("900", handler.LastRequestBody!);
        Assert.Contains("KazamControl.Process", handler.LastRequestBody!);
        Assert.NotEqual(WarehouseRoutes.MissionServedAssignment, WarehouseRoutes.ConfirmServedAssignments);
        Assert.NotEqual(WarehouseRoutes.LoadUnitLeftExtractionQueue, WarehouseRoutes.ConfirmServedAssignments);
    }

    /// <summary>
    /// <b>Il cambiamento dichiarato del compito 19:</b> su questo percorso l'errore veniva
    /// inghiottito nel motore, e adesso e' un guasto come tutti gli altri del confine.
    /// </summary>
    [Fact]
    public async Task AServerErrorConfirmingTheServedAssignmentsBecomesWarehouseUnavailable()
    {
        var warehouse = Build(HttpStatusCode.InternalServerError);

        var ex = await Assert.ThrowsAsync<WarehouseUnavailableException>(
            () => warehouse.ConfirmServedAssignmentsAsync(new ConfirmServedAssignmentsRequest(900, "KazamControl.Process")));

        Assert.Equal("ConfirmServedAssignments", ex.Operation);
    }

    /// <summary>
    /// Voce 45: il fatto dell'estrazione prelevata diventata deposito viaggia con una POST sulla sua
    /// rotta, con tutti i campi della richiesta nel corpo, e l'esito si legge dal corpo del 200 -
    /// uno per l'assegnazione e uno per la riga di estrazione, piu' il dettaglio. I nomi del corpo
    /// della risposta sono in minuscola iniziale e il dettaglio nullo manca del tutto, come li scrive
    /// il gestionale (Newtonsoft con <c>NullValueHandling.Ignore</c>).
    /// </summary>
    [Theory]
    [InlineData("""{"assignment":0,"extractionRow":1}""", PickedExtractionReopenOutcome.Reopened, PickedExtractionReopenOutcome.NothingToReopen, null)]
    [InlineData("""{"assignment":1,"extractionRow":0,"detail":"tipo nota non riattivabile"}""", PickedExtractionReopenOutcome.NothingToReopen, PickedExtractionReopenOutcome.Reopened, "tipo nota non riattivabile")]
    [InlineData("""{"assignment":2,"extractionRow":2,"detail":"spedizione spedita"}""", PickedExtractionReopenOutcome.OrderAlreadyClosed, PickedExtractionReopenOutcome.OrderAlreadyClosed, "spedizione spedita")]
    public async Task ThePickedExtractionBecameDepositIsAPostOnItsRouteAndItsOutcomesAreInTheBody(
        string risposta, PickedExtractionReopenOutcome assegnazione, PickedExtractionReopenOutcome riga, string? dettaglio)
    {
        var handler = new StubHandler(HttpStatusCode.OK, risposta);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var warehouse = new HttpWarehouse(http, new HttpWarehouseOptions { ApiKey = "prova" });

        var esito = await warehouse.ReportPickedExtractionBecameDepositAsync(new PickedExtractionBecameDepositRequest(
            Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444"),
            Guid.Parse("bbbbbbbb-1111-2222-3333-444444444444"),
            4711m,
            902m,
            Guid.Parse("cccccccc-1111-2222-3333-444444444444"),
            8045m,
            "KazamControl.Process",
            "mandata in magazzino dalla pagina"));

        Assert.Equal(new PickedExtractionBecameDepositResult(assegnazione, riga, dettaglio), esito);

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/api/wcs/warehouse/assignments/picked-extraction-became-deposit",
            handler.LastRequest.RequestUri!.AbsolutePath);

        // Maiuscola iniziale: e' la POST che aspetta una risposta (vedi la ricerca per referenza).
        var corpo = handler.LastRequestBody!;
        Assert.Contains("\"OperationId\":\"aaaaaaaa-1111-2222-3333-444444444444\"", corpo);
        Assert.Contains("\"MissionId\":\"bbbbbbbb-1111-2222-3333-444444444444\"", corpo);
        Assert.Contains("\"LoadUnitId\":4711", corpo);
        Assert.Contains("\"AssignmentId\":902", corpo);
        Assert.Contains("\"AssignmentGuid\":\"cccccccc-1111-2222-3333-444444444444\"", corpo);
        Assert.Contains("\"ExtractionRowId\":8045", corpo);
        Assert.Contains("\"UserId\":\"KazamControl.Process\"", corpo);
        Assert.Contains("\"Reason\":\"mandata in magazzino dalla pagina\"", corpo);
    }

    /// <summary>
    /// Voce 45: un 200 senza i due esiti non e' «riaperto». Il valore 0 dell'esito e'
    /// <see cref="PickedExtractionReopenOutcome.Reopened"/>, e un corpo vuoto letto con i default
    /// direbbe alla coda del WCS di togliere un fatto che il gestionale non ha applicato: e' un corpo
    /// che non si legge, cioe' un guasto.
    /// </summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"assignment":1}""")]
    [InlineData("""{"extractionRow":1}""")]
    public async Task APickedExtractionAnswerWithoutBothOutcomesBecomesWarehouseUnavailable(string risposta)
    {
        var warehouse = Build(HttpStatusCode.OK, risposta);

        var ex = await Assert.ThrowsAsync<WarehouseUnavailableException>(
            () => warehouse.ReportPickedExtractionBecameDepositAsync(PickedExtractionRequest()));

        Assert.Equal("ReportPickedExtractionBecameDeposit", ex.Operation);
        Assert.IsType<JsonException>(ex.InnerException);
    }

    /// <summary>
    /// Voce 45: il gestionale muto e' un guasto da ritentare, il 409 dello stesso identificativo con
    /// un contenuto diverso e' un rifiuto. Tutt'e due arrivano come
    /// <see cref="WarehouseUnavailableException"/>, e la coda li separa con
    /// <see cref="WarehouseUnavailableException.IsPermanentRejection"/>.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    [InlineData(HttpStatusCode.Conflict, true)]
    public async Task AFailedPickedExtractionReportBecomesWarehouseUnavailable(HttpStatusCode stato, bool rifiuto)
    {
        var warehouse = Build(stato);

        var ex = await Assert.ThrowsAsync<WarehouseUnavailableException>(
            () => warehouse.ReportPickedExtractionBecameDepositAsync(PickedExtractionRequest()));

        Assert.Equal("ReportPickedExtractionBecameDeposit", ex.Operation);
        Assert.Equal(stato, ex.StatusCode);
        Assert.Equal(rifiuto, ex.IsPermanentRejection);
    }

    private static PickedExtractionBecameDepositRequest PickedExtractionRequest() =>
        new(Guid.NewGuid(), Guid.NewGuid(), 4711m, 902m, Guid.NewGuid(), 8045m, "KazamControl.Process", "prova");

    /// <summary>
    /// Il codice dell'unita' di carico e' un segmento del percorso, e un segmento vuoto non lega un
    /// parametro obbligatorio: <c>load-units/by-code//stored</c> non corrisponde a nessuna rotta.
    /// Il contratto promette falso su un codice vuoto, e il client deve mantenerlo lui, senza
    /// mandare una richiesta che il gestionale non puo' servire.
    /// </summary>
    /// <remarks>
    /// Il gestore finto risponde 500 apposta: se la richiesta partisse, la lettura severa la
    /// tradurrebbe in <c>WarehouseUnavailableException</c> e il collaudo sarebbe rosso. Come era:
    /// il 404 di instradamento faceva esattamente questo, e la guardia scritta nel gestionale non
    /// veniva mai raggiunta.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task AnEmptyLoadUnitCodeIsAnsweredWithoutAskingTheWarehouse(string? code)
    {
        var handler = new StubHandler(HttpStatusCode.InternalServerError, "{}");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var warehouse = new HttpWarehouse(http, new HttpWarehouseOptions { ApiKey = "prova" });

        Assert.False(await warehouse.IsLoadUnitStoredAsync(code));
        Assert.Null(await warehouse.ResolveLoadUnitByCodeAsync(code));
        Assert.Null(handler.LastRequest);
    }

    /// <summary>
    /// La meta' che deve riuscire: senza questa, un client che non chiamasse mai il gestionale
    /// passerebbe il collaudo qui sopra. Con un codice vero la domanda parte, sulla rotta giusta.
    /// </summary>
    [Fact]
    public async Task ARealLoadUnitCodeIsAskedToTheWarehouseOnItsOwnRoute()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "true");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var warehouse = new HttpWarehouse(http, new HttpWarehouseOptions { ApiKey = "prova" });

        Assert.True(await warehouse.IsLoadUnitStoredAsync("UDC-042"));

        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.EndsWith($"{WarehouseRoutes.Prefix}/load-units/by-code/UDC-042/stored",
            handler.LastRequest.RequestUri!.AbsolutePath);
    }

    /// <summary>
    /// La chiave e' il <b>tipo</b> di unita' di carico, non l'unita' di carico: la rotta e' una sua,
    /// e il test la guarda perche' un percorso che scivolasse su <c>load-units/{id}/description</c>
    /// risponderebbe lo stesso un 200 con un corpo che si legge.
    /// </summary>
    [Fact]
    public async Task GetLoadUnitTypeHandlingModeReadsTheTypeRoute()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"loadUnitTypeId":7,"handlingMode":2}""");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var warehouse = new HttpWarehouse(http, new HttpWarehouseOptions { ApiKey = "prova" });

        var mode = await warehouse.GetLoadUnitTypeHandlingModeAsync(7);

        Assert.Equal(new LoadUnitTypeHandlingMode(7, 2), mode);
        Assert.EndsWith("/load-unit-types/7/handling-mode", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    /// <summary>Un tipo che non esiste e' una risposta di dominio, non un guasto: 404 letto come «non c'e'».</summary>
    [Fact]
    public async Task GetLoadUnitTypeHandlingModeOfAMissingTypeIsNull()
    {
        var warehouse = Build(HttpStatusCode.NotFound);
        Assert.Null(await warehouse.GetLoadUnitTypeHandlingModeAsync(7));
    }

    /// <summary>
    /// Compito 2: le aree servite dal polmone arrivano <b>nell'ordine in cui il gestionale le
    /// manda</b>, che e' quello di <c>PRIORITA</c>. Il corpo e' volutamente disordinato rispetto al
    /// valore numerico: un client che le raccogliesse in un insieme, o che le riordinasse, darebbe
    /// al motore una precedenza di prelievo diversa da quella configurata e il test non se ne
    /// accorgerebbe se la lista fosse gia' crescente.
    /// </summary>
    [Fact]
    public async Task GetPickingBufferAreasKeepsThePriorityOrder()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "[3,1,2]");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var warehouse = new HttpWarehouse(http, new HttpWarehouseOptions { ApiKey = "prova" });

        var areas = await warehouse.GetPickingBufferAreasAsync(44);

        Assert.Equal(new decimal[] { 3, 1, 2 }, areas);
        Assert.EndsWith($"{WarehouseRoutes.Prefix}/cells/44/picking-buffer-areas",
            handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    /// <summary>
    /// Una cella che non serve nessuna area risponde 200 con <c>[]</c>, ed e' un dato: il motore ne
    /// trae che la missione non ha dove andare. Un 404 su questa rotta non e' invece l'assenza ma un
    /// guasto - non esiste una «cella senza aree» che il gestionale segnali cosi' - e la lettura e'
    /// severa apposta.
    /// </summary>
    [Fact]
    public async Task GetPickingBufferAreasOfACellWithoutAreasIsAnEmptyListAndA404IsAFailure()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "[]");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };

        Assert.Empty(await new HttpWarehouse(http, new HttpWarehouseOptions { ApiKey = "prova" })
            .GetPickingBufferAreasAsync(44));

        await Assert.ThrowsAsync<WarehouseUnavailableException>(
            () => Build(HttpStatusCode.NotFound).GetPickingBufferAreasAsync(44));
    }

    /// <summary>
    /// Compito 10: la topologia del magazzino. Il corpo interessante e' <c>cellToArea</c>, un
    /// dizionario con chiavi numeriche che JSON scrive come stringhe: il test lo legge davvero -
    /// non solo l'elenco delle aree - perche' e' la meta' che il motore incrocia con la mappa
    /// porte, e un dizionario che tornasse vuoto darebbe «nessuna area automatica collegata»
    /// senza che niente lo segnali.
    /// </summary>
    [Fact]
    public async Task GetAreaTopologyReadsTheWarehouseTopologyRoute()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"warehouseId":1,"automaticAreaIds":[10,30],"cellToArea":{"100":10,"300":30}}""");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var warehouse = new HttpWarehouse(http, new HttpWarehouseOptions { ApiKey = "prova" });

        var topology = await warehouse.GetAreaTopologyAsync(1);

        Assert.Equal(1, topology.WarehouseId);
        Assert.Equal(new decimal[] { 10, 30 }, topology.AutomaticAreaIds);
        Assert.Equal(new Dictionary<decimal, decimal> { [100] = 10, [300] = 30 }, topology.CellToArea);
        Assert.EndsWith($"{WarehouseRoutes.Prefix}/warehouses/1/topology",
            handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    /// <summary>
    /// Un magazzino che non esiste non e' un'assenza di dominio su questa rotta: chi la chiama ha
    /// gia' in mano un identificativo che viene dal gestionale (la cella collegata al dispositivo),
    /// quindi un 404 e' uno stato inatteso come ogni altro e la lettura e' <b>severa</b>. Un
    /// magazzino senza aree automatiche si dice invece con un 200 e gli elenchi vuoti.
    /// </summary>
    [Fact]
    public async Task GetAreaTopologyOfAMissingWarehouseIsAFailure() =>
        await Assert.ThrowsAsync<WarehouseUnavailableException>(
            () => Build(HttpStatusCode.NotFound).GetAreaTopologyAsync(1));

    /// <summary>
    /// Compito 3: l'unita' di carico assegnata a una nota. La chiave e' l'unita' di carico e la
    /// rotta e' una sua: il test guarda il percorso perche' una richiesta che scivolasse su
    /// <c>load-units/{id}/description</c> risponderebbe lo stesso un 200 con un corpo che si legge.
    /// </summary>
    [Fact]
    public async Task GetLoadUnitAssignmentStateReadsTheAssignmentStateRoute()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"loadUnitId":6100,"assignedToNote":true}""");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var warehouse = new HttpWarehouse(http, new HttpWarehouseOptions { ApiKey = "prova" });

        var state = await warehouse.GetLoadUnitAssignmentStateAsync(6100);

        Assert.Equal(new LoadUnitAssignmentState(6100, true), state);
        Assert.EndsWith($"{WarehouseRoutes.Prefix}/load-units/6100/assignment-state",
            handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    /// <summary>
    /// «Non assegnata» e' un dato, e arriva con un 200: un'unita' di carico che non compare in
    /// <c>ASSEGNAZIONE_NOTE</c> - compresa quella che non esiste - e' una risposta, non un'assenza.
    /// La lettura e' quindi <b>severa</b>: un 404 su questa rotta e' un guasto come ogni altro stato
    /// inatteso, e non si puo' leggere come «libera» - leggerlo cosi' farebbe partire lo spostamento
    /// a mano di un'unita' di carico che una nota sta aspettando, che e' esattamente il difetto di
    /// <c>DbInfo.IsLuPresentInAssignmentNote</c>.
    /// </summary>
    [Fact]
    public async Task GetLoadUnitAssignmentStateOfAFreeLoadUnitIsFalseAndA404IsAFailure()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"loadUnitId":6100,"assignedToNote":false}""");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };

        Assert.False((await new HttpWarehouse(http, new HttpWarehouseOptions { ApiKey = "prova" })
            .GetLoadUnitAssignmentStateAsync(6100)).AssignedToNote);

        await Assert.ThrowsAsync<WarehouseUnavailableException>(
            () => Build(HttpStatusCode.NotFound).GetLoadUnitAssignmentStateAsync(6100));
    }

    /// <summary>
    /// Compito 4: la chiave e' la <b>coppia</b> area piu' tipo di unita' di carico, e il percorso la
    /// porta tutta - l'area nel percorso, il tipo nella query string. Il test guarda tutt'e due i
    /// pezzi: un client che dimenticasse il tipo chiederebbe lo stesso un'area, riceverebbe lo
    /// stesso un 200 leggibile, e risponderebbe il verdetto di un tipo qualunque.
    /// <para>
    /// L'identificativo e' un <c>decimal</c>: la query string si scrive in cultura invariante, o su
    /// una macchina con la virgola decimale un tipo come <c>7,5</c> partirebbe illeggibile. Il test
    /// lo mostra con un identificativo che ha una parte frazionaria.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GetAreaDefragmentationVerdictReadsTheAreaRouteWithTheTypeInTheQueryString()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"areaId":12,"loadUnitTypeId":7,"needed":true}""");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var warehouse = new HttpWarehouse(http, new HttpWarehouseOptions { ApiKey = "prova" });

        var verdict = await warehouse.GetAreaDefragmentationVerdictAsync(12, 7);

        Assert.Equal(new AreaDefragmentationVerdict(12, 7, true), verdict);
        Assert.EndsWith($"{WarehouseRoutes.Prefix}/areas/12/defragmentation", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("?loadUnitTypeId=7", handler.LastRequest.RequestUri.Query);

        await warehouse.GetAreaDefragmentationVerdictAsync(12, 7.5m);
        Assert.Equal("?loadUnitTypeId=7.5", handler.LastRequest.RequestUri!.Query);
    }

    /// <summary>
    /// «Non serve» e' un dato, e arriva con un 200: un'area che non ha celle di quel tipo non va
    /// deframmentata, e quella e' la risposta. La lettura e' quindi <b>severa</b>: un 404 su questa
    /// rotta e' un guasto come ogni altro stato inatteso, e non si puo' leggere come «non serve» -
    /// leggerlo cosi' e' esattamente il difetto di <c>DbInfo.IsCellToDefragmentByLuType</c>, che
    /// sull'errore della lettura rispondeva falso.
    /// </summary>
    [Fact]
    public async Task GetAreaDefragmentationVerdictOfATidyAreaIsFalseAndA404IsAFailure()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"areaId":12,"loadUnitTypeId":7,"needed":false}""");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };

        Assert.False((await new HttpWarehouse(http, new HttpWarehouseOptions { ApiKey = "prova" })
            .GetAreaDefragmentationVerdictAsync(12, 7)).Needed);

        await Assert.ThrowsAsync<WarehouseUnavailableException>(
            () => Build(HttpStatusCode.NotFound).GetAreaDefragmentationVerdictAsync(12, 7));
    }

    /// <summary>
    /// Compito 5: gli identificativi viaggiano nel <b>corpo</b> di un POST, e la risposta e' il
    /// sottoinsieme in pausa. Il test guarda il corpo e non solo il percorso: una richiesta che
    /// partisse senza gli identificativi arriverebbe lo stesso alla rotta giusta e riceverebbe lo
    /// stesso un 200 leggibile - una lista vuota, che il motore leggerebbe come «nessuna in pausa».
    /// <para>
    /// Il corpo si rilegge con le opzioni <see cref="JsonSerializerDefaults.Web"/>, cioe' in modo
    /// insensibile alle maiuscole, invece di confrontare il testo: <c>PostAsync</c> con risposta
    /// serializza con le opzioni predefinite (nomi in maiuscola iniziale) mentre quello senza
    /// risposta usa <c>JsonContent.Create</c> (nomi in minuscola iniziale), e a tenere insieme i due
    /// lati e' la lettura insensibile alle maiuscole del lato gestionale - che non usa
    /// System.Text.Json ma Newtonsoft (<c>AddNewtonsoftJson</c> nel suo <c>Program.cs</c>), anch'esso
    /// insensibile alle maiuscole. E' quella proprieta' che serve provare - gli identificativi
    /// arrivano leggibili - non la forma esatta delle lettere.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GetPausedExtractionRowsPostsTheIdsAndReadsTheSubsetBack()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "[502,504]");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var warehouse = new HttpWarehouse(http, new HttpWarehouseOptions { ApiKey = "prova" });

        var paused = await warehouse.GetPausedExtractionRowsAsync([501m, 502m, 503m, 504m]);

        Assert.Equal(new decimal[] { 502, 504 }, paused);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.EndsWith($"{WarehouseRoutes.Prefix}/extractions/paused-among",
            handler.LastRequest.RequestUri!.AbsolutePath);

        var inviata = JsonSerializer.Deserialize<PausedAmongRequest>(
            handler.LastRequestBody!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(new decimal[] { 501, 502, 503, 504 }, inviata!.Ids);
    }

    /// <summary>
    /// L'elenco vuoto non e' un giro di rete: la risposta e' una lista vuota e la richiesta non
    /// parte proprio. Il gestore finto risponderebbe <c>[]</c> lo stesso, quindi la prova sta
    /// sull'<b>assenza</b> della richiesta - <c>LastRequest</c> ancora nullo.
    /// </summary>
    [Fact]
    public async Task GetPausedExtractionRowsOfAnEmptyListDoesNotCallTheWarehouse()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "[]");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };

        Assert.Empty(await new HttpWarehouse(http, new HttpWarehouseOptions { ApiKey = "prova" })
            .GetPausedExtractionRowsAsync([]));

        Assert.Null(handler.LastRequest);
    }

    /// <summary>
    /// Lettura <b>severa</b>: «nessuna in pausa» e' la lista vuota con un 200, quindi un 404 su
    /// questa rotta e' un guasto come ogni altro stato inatteso e non si puo' leggere come
    /// «nessuna» - leggerlo cosi' e' il difetto di <c>PausedAmongAsync</c>, che sull'errore della
    /// lettura rispondeva <see langword="null"/> e faceva saltare la pulizia della coda in silenzio.
    /// </summary>
    [Fact]
    public async Task GetPausedExtractionRowsWithNothingPausedIsAnEmptyListAndA404IsAFailure()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "[]");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };

        Assert.Empty(await new HttpWarehouse(http, new HttpWarehouseOptions { ApiKey = "prova" })
            .GetPausedExtractionRowsAsync([501m]));

        var ex = await Assert.ThrowsAsync<WarehouseUnavailableException>(
            () => Build(HttpStatusCode.NotFound).GetPausedExtractionRowsAsync([501m]));

        Assert.Equal("GetPausedExtractionRows", ex.Operation);
    }

    /// <summary>
    /// Compito 6: la gemella delle note, stessa forma e <b>rotta diversa</b> - e' la rotta a
    /// distinguere le due domande, visto che il corpo e' lo stesso <see cref="PausedAmongRequest"/>.
    /// Il test guarda la rotta e il corpo: una chiamata che finisse su <c>extractions/paused-among</c>
    /// riceverebbe una risposta leggibile e plausibile, e sarebbe la risposta a un'altra domanda.
    /// <para>
    /// Il corpo si rilegge con le opzioni <see cref="JsonSerializerDefaults.Web"/>, cioe' in modo
    /// insensibile alle maiuscole, per la stessa ragione della gemella: e' cosi' che lo legge il
    /// gestionale (Newtonsoft), e la forma esatta delle lettere non e' la proprieta' da provare.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GetPausedNoteAssignmentsPostsTheIdsAndReadsTheSubsetBack()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "[902,904]");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var warehouse = new HttpWarehouse(http, new HttpWarehouseOptions { ApiKey = "prova" });

        var paused = await warehouse.GetPausedNoteAssignmentsAsync([901m, 902m, 903m, 904m]);

        Assert.Equal(new decimal[] { 902, 904 }, paused);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.EndsWith($"{WarehouseRoutes.Prefix}/notes/paused-among",
            handler.LastRequest.RequestUri!.AbsolutePath);

        var inviata = JsonSerializer.Deserialize<PausedAmongRequest>(
            handler.LastRequestBody!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(new decimal[] { 901, 902, 903, 904 }, inviata!.Ids);
    }

    /// <summary>
    /// L'elenco vuoto non e' un giro di rete, come per la gemella: la prova sta sull'<b>assenza</b>
    /// della richiesta, perche' il gestore finto risponderebbe <c>[]</c> lo stesso.
    /// </summary>
    [Fact]
    public async Task GetPausedNoteAssignmentsOfAnEmptyListDoesNotCallTheWarehouse()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "[]");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };

        Assert.Empty(await new HttpWarehouse(http, new HttpWarehouseOptions { ApiKey = "prova" })
            .GetPausedNoteAssignmentsAsync([]));

        Assert.Null(handler.LastRequest);
    }

    /// <summary>
    /// Lettura <b>severa</b>: «nessuna in pausa» e' la lista vuota con un 200, quindi un 404 e' un
    /// guasto come ogni altro stato inatteso. Leggerlo come «nessuna» era il difetto di
    /// <c>PausedAmongAsync</c>, che sull'errore della lettura rispondeva <see langword="null"/> e
    /// faceva saltare la pulizia della coda in silenzio.
    /// </summary>
    [Fact]
    public async Task GetPausedNoteAssignmentsWithNothingPausedIsAnEmptyListAndA404IsAFailure()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "[]");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };

        Assert.Empty(await new HttpWarehouse(http, new HttpWarehouseOptions { ApiKey = "prova" })
            .GetPausedNoteAssignmentsAsync([901m]));

        var ex = await Assert.ThrowsAsync<WarehouseUnavailableException>(
            () => Build(HttpStatusCode.NotFound).GetPausedNoteAssignmentsAsync([901m]));

        Assert.Equal("GetPausedNoteAssignments", ex.Operation);
    }

    /// <summary>
    /// Compito 7: le celle dell'elenco dell'Editor. I due filtri stanno nella <b>query string</b> e
    /// il percorso non ha segmenti, perche' la domanda non ha una chiave: e' l'elenco, e i filtri
    /// sono il modo di guardarlo. Il test guarda percorso e query insieme
    /// (<c>PathAndQuery</c>): un filtro perso per strada lascia la rotta giusta e una risposta
    /// plausibile, e la finestra dei collegamenti mostrerebbe celle di aree che non c'entrano.
    /// </summary>
    [Fact]
    public async Task GetCellsForEditorPutsTheTwoFiltersInTheQueryString()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            [{"cellId":300,"warehouseDescription":"Magazzino 1","areaDescription":"Area A","column":4,
              "floor":2,"doorCodeA":"P01A","doorCodeB":"P01B","description":"Cella 4-2"}]
            """);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var warehouse = new HttpWarehouse(http, new HttpWarehouseOptions { ApiKey = "prova" });

        var cells = await warehouse.GetCellsForEditorAsync(2, automaticOnly: true);

        Assert.Equal(new EditorCellRow(300, "Magazzino 1", "Area A", 4, 2, "P01A", "P01B", "Cella 4-2"), Assert.Single(cells));
        Assert.Equal("/api/wcs/warehouse/cells?areaType=2&automaticOnly=true", handler.LastRequest!.RequestUri!.PathAndQuery);
    }

    /// <summary>
    /// Senza tipo d'area il filtro <b>non parte</b>, invece di partire vuoto: «nessun tipo» vuol
    /// dire le aree di stoccaggio (0 e 1), ed e' una risposta diversa da «tipo zero». Un
    /// <c>areaType=</c> vuoto la lascerebbe decidere al legame di modello del gestionale, che e'
    /// tollerante oggi e non e' detto che lo resti.
    /// </summary>
    [Fact]
    public async Task WithoutAnAreaTypeTheFilterDoesNotTravelAtAll()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "[]");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };

        Assert.Empty(await new HttpWarehouse(http, new HttpWarehouseOptions { ApiKey = "prova" })
            .GetCellsForEditorAsync(null, automaticOnly: false));

        Assert.Equal("/api/wcs/warehouse/cells?automaticOnly=false", handler.LastRequest!.RequestUri!.PathAndQuery);
    }

    /// <summary>
    /// Lettura <b>severa</b>: un impianto senza celle di quel tipo risponde <c>[]</c> con un 200,
    /// quindi non c'e' nessun «non c'e'» da tradurre e un 404 e' un guasto. L'Editor deve dire che
    /// non si e' potuto leggere, non aprire una finestra vuota che sembra un impianto da
    /// configurare.
    /// </summary>
    [Fact]
    public async Task GetCellsForEditorWithNoCellsIsAnEmptyListAndA404IsAFailure()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "[]");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };

        Assert.Empty(await new HttpWarehouse(http, new HttpWarehouseOptions { ApiKey = "prova" })
            .GetCellsForEditorAsync(2, automaticOnly: true));

        var ex = await Assert.ThrowsAsync<WarehouseUnavailableException>(
            () => Build(HttpStatusCode.NotFound).GetCellsForEditorAsync(2, automaticOnly: true));

        Assert.Equal("GetCellsForEditor", ex.Operation);
    }

    /// <summary>
    /// Compito 7: il controllo di configurazione. Le colonne le decide la procedura del
    /// gestionale, quindi la riga arriva come un dizionario e si legge cosi' com'e': il test
    /// guarda che le chiavi non si perdano nel passaggio, perche' sono quelle a diventare le
    /// colonne della griglia dell'Editor.
    /// </summary>
    [Fact]
    public async Task GetConfigurationWarningsReadsTheColumnsTheProcedureDecidesAndSendsTheMode()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            [{"columns":{"priority":"3","message":"Magazzino non creato","note":null}}]
            """);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var warehouse = new HttpWarehouse(http, new HttpWarehouseOptions { ApiKey = "prova" });

        var warnings = await warehouse.GetConfigurationWarningsAsync(2);

        var columns = Assert.Single(warnings).Columns;
        Assert.Equal("3", columns["priority"]);
        Assert.Equal("Magazzino non creato", columns["message"]);
        Assert.Null(columns["note"]);
        Assert.Equal("/api/wcs/warehouse/configuration-check?mode=2", handler.LastRequest!.RequestUri!.PathAndQuery);
    }

    /// <summary>
    /// Anche questa e' severa, e per una ragione che si vede a occhio: <b>nessuna riga vuol dire
    /// configurazione a posto</b>. Un 404 letto come lista vuota direbbe all'Editor che l'impianto
    /// e' configurato bene proprio quando il gestionale non ha risposto.
    /// </summary>
    [Fact]
    public async Task GetConfigurationWarningsWithNothingWrongIsAnEmptyListAndA404IsAFailure()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "[]");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };

        Assert.Empty(await new HttpWarehouse(http, new HttpWarehouseOptions { ApiKey = "prova" })
            .GetConfigurationWarningsAsync(0));

        var ex = await Assert.ThrowsAsync<WarehouseUnavailableException>(
            () => Build(HttpStatusCode.NotFound).GetConfigurationWarningsAsync(0));

        Assert.Equal("GetConfigurationWarnings", ex.Operation);
    }

    private static (HttpWarehouse Warehouse, StubHandler Handler) BuildWithHandler(HttpStatusCode code, string body)
    {
        var handler = new StubHandler(code, body);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        return (new HttpWarehouse(http, new HttpWarehouseOptions { ApiKey = "prova" }), handler);
    }

    private static ReleaseLoadUnitCellRequest GiveBack() => new(
        Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444"),
        Guid.Parse("bbbbbbbb-1111-2222-3333-444444444444"),
        4711m, 6m, "KazamControl.Process", "redirected to OUT02");

    /// <summary>
    /// Item 54, task 5: the give-back of a load unit's cell is a POST on its route, and the outcome is
    /// the number in the body of the 200.
    /// </summary>
    [Theory]
    [InlineData("0", LoadUnitCellReleaseOutcome.Released)]
    [InlineData("1", LoadUnitCellReleaseOutcome.NotHeld)]
    public async Task TheGiveBackOfALoadUnitsCellIsAPostOnItsRouteAndItsOutcomeIsInTheBody(string body, LoadUnitCellReleaseOutcome expected)
    {
        var (warehouse, handler) = BuildWithHandler(HttpStatusCode.OK, body);

        var outcome = await warehouse.ReleaseLoadUnitCellAsync(GiveBack());

        Assert.Equal(expected, outcome);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/api/wcs/warehouse/reservations/release-load-unit-cell", handler.LastRequest.RequestUri!.AbsolutePath);
        var sent = handler.LastRequestBody!;
        Assert.Contains("\"OperationId\":\"aaaaaaaa-1111-2222-3333-444444444444\"", sent);
        Assert.Contains("\"MissionId\":\"bbbbbbbb-1111-2222-3333-444444444444\"", sent);
        Assert.Contains("\"LoadUnitId\":4711", sent);
        Assert.Contains("\"CellId\":6", sent);
        Assert.Contains("\"UserId\":\"KazamControl.Process\"", sent);
        Assert.Contains("\"Reason\":\"redirected to OUT02\"", sent);
    }

    /// <summary>
    /// The warehouse mute is a failure that heals, a 4xx a rejection the queue parks, and a 200 without
    /// an outcome is not «released»: it does not read, and is a failure.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "{}", false)]
    [InlineData(HttpStatusCode.BadRequest, "{}", true)]
    [InlineData(HttpStatusCode.OK, "", false)]
    [InlineData(HttpStatusCode.OK, "{}", false)]
    public async Task AFailedGiveBackOfALoadUnitsCellBecomesWarehouseUnavailable(HttpStatusCode status, string body, bool rejection)
    {
        var ex = await Assert.ThrowsAsync<WarehouseUnavailableException>(
            () => Build(status, body).ReleaseLoadUnitCellAsync(GiveBack()));

        Assert.Equal("ReleaseLoadUnitCell", ex.Operation);
        Assert.Equal(rejection, ex.IsPermanentRejection);
    }

    /// <summary>
    /// Item 54, task 5: the candidate cells are a POST although a reading - the search lists travel in
    /// the body - and the answer is the list in the warehouse's order.
    /// </summary>
    [Fact]
    public async Task TheCandidateCellsAreAPostOnTheirRouteAndTheListKeepsItsOrder()
    {
        var (warehouse, handler) = BuildWithHandler(HttpStatusCode.OK, """
            [{"cellId":363,"doorCode":"3B02.0363.0001","areaId":10,"areaDescription":"Traslo 1","column":63,"floor":2,"loadUnitsInCell":1},
             {"cellId":12,"doorCode":"3B02.0012.0001","areaId":10,"areaDescription":"Traslo 1","column":12,"floor":1,"description":"bassa","loadUnitsInCell":0}]
            """);

        var cells = await warehouse.ListStorageCellCandidatesAsync(new StorageCellCandidatesRequest(4711m, 3m, [10m], [369m],
            [Guid.Parse("cccccccc-1111-2222-3333-444444444444")], StorageCellSearchMode.FreeAutomaticOperator));

        Assert.Equal(
        [
            new StorageCellCandidate(363m, "3B02.0363.0001", 10m, "Traslo 1", 63, 2, null, 1),
            new StorageCellCandidate(12m, "3B02.0012.0001", 10m, "Traslo 1", 12, 1, "bassa", 0)
        ], cells);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/api/wcs/warehouse/reservations/candidates", handler.LastRequest.RequestUri!.AbsolutePath);
        var sent = handler.LastRequestBody!;
        Assert.Contains("\"LoadUnitId\":4711", sent);
        Assert.Contains("\"WarehouseId\":3", sent);
        Assert.Contains("\"SearchAreaIds\":[10]", sent);
        Assert.Contains("\"ExcludedCellIds\":[369]", sent);
        Assert.Contains("\"ReachableDeviceIds\":[\"cccccccc-1111-2222-3333-444444444444\"]", sent);
        Assert.Contains("\"CellSearchMode\":1", sent);
    }

    /// <summary>The answer is a list: a 404 is not «no cell», it is a failure like any unexpected status.</summary>
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task AFailedCandidateListBecomesWarehouseUnavailable(HttpStatusCode status)
    {
        var ex = await Assert.ThrowsAsync<WarehouseUnavailableException>(() => Build(status).ListStorageCellCandidatesAsync(
            new StorageCellCandidatesRequest(4711m, 3m, [], [], [], StorageCellSearchMode.All)));

        Assert.Equal("ListStorageCellCandidates", ex.Operation);
    }

    /// <summary>
    /// Item 54, task 5: the reservation of a precise cell is a POST on its route; the reservation and the
    /// refusals are in the body of the 200.
    /// </summary>
    [Fact]
    public async Task TheReservationOfAPreciseCellIsAPostOnItsRouteAndItsOutcomeIsInTheBody()
    {
        var mission = Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444");
        var (warehouse, handler) = BuildWithHandler(HttpStatusCode.OK, """
            {"outcome":0,"reservation":{"missionId":"aaaaaaaa-1111-2222-3333-444444444444","outcome":0,"cellId":363,"doorCode":"3B02.0363.0001","partition":"0000","storageDepth":1200,"needsCompaction":false}}
            """);

        var result = await warehouse.ReserveStorageCellAsync(
            new ReserveStorageCellRequest(mission, 4711m, "3B02.0363.0001", "WCS", "KazamControl.Process"));

        Assert.Equal(new StorageCellReservationResult(StorageCellReservationOutcome.Reserved,
            new StorageReservation(mission, StorageReservationOutcome.CellFound, 363m, "3B02.0363.0001", "0000", 1200, false), null), result);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/api/wcs/warehouse/reservations/cell", handler.LastRequest.RequestUri!.AbsolutePath);
        var sent = handler.LastRequestBody!;
        Assert.Contains("\"MissionId\":\"aaaaaaaa-1111-2222-3333-444444444444\"", sent);
        Assert.Contains("\"LoadUnitId\":4711", sent);
        Assert.Contains("\"DoorCode\":\"3B02.0363.0001\"", sent);
        Assert.Contains("\"RequestedByWorkstationId\":\"WCS\"", sent);
        Assert.Contains("\"RequestedByUserId\":\"KazamControl.Process\"", sent);
    }

    [Fact]
    public async Task ARefusedPreciseCellIsAnAnswerWithItsReason()
    {
        var result = await Build(HttpStatusCode.OK, """{"outcome":2,"reason":"porta B della cella 363"}""")
            .ReserveStorageCellAsync(new ReserveStorageCellRequest(Guid.NewGuid(), 4711m, "3B02.0363.0002", "WCS", "u"));

        Assert.Equal(new StorageCellReservationResult(StorageCellReservationOutcome.NotADoorA, null, "porta B della cella 363"), result);
    }

    /// <summary>A 200 without the outcome would read «reserved»: it does not read, and is a failure; so is a 500.</summary>
    [Theory]
    [InlineData(HttpStatusCode.OK, "{}")]
    [InlineData(HttpStatusCode.InternalServerError, "{}")]
    public async Task AFailedReservationOfAPreciseCellBecomesWarehouseUnavailable(HttpStatusCode status, string body)
    {
        var ex = await Assert.ThrowsAsync<WarehouseUnavailableException>(() => Build(status, body)
            .ReserveStorageCellAsync(new ReserveStorageCellRequest(Guid.NewGuid(), 4711m, "3B02.0363.0001", "WCS", "u")));

        Assert.Equal("ReserveStorageCell", ex.Operation);
    }
}
