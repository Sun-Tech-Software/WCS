namespace Wcs.Wms.Contracts.Test;

/// <summary>
/// Il gestionale nei test: risponde quello che gli si dice di rispondere, conta le chiamate, e
/// può fingere di non rispondere.
/// </summary>
public sealed class FakeWarehouse : IWarehouse
{
    public List<string> Calls { get; } = [];
    public bool Unavailable { get; set; }

    public Dictionary<decimal, CellDescription> Cells { get; } = [];
    public Dictionary<decimal, LoadUnitLocation> LoadUnitLocations { get; } = [];
    public Dictionary<decimal, StorageReservation> StorageReservations { get; } = [];

    /// <summary>
    /// Le prenotazioni di deposito tenute, indicizzate per missione (compito 17): e' la memoria che
    /// nel gestionale vero e' il legame fra missione e cella. <see cref="ReserveStorageAsync"/> la
    /// riempie, <see cref="GetStorageReservationAsync"/> la legge,
    /// <see cref="ReleaseStorageReservationAsync"/> la svuota.
    /// </summary>
    public Dictionary<Guid, StorageReservation> StorageReservationsByMission { get; } = [];

    /// <summary>Le missioni per cui il motore ha chiesto il rilascio, nell'ordine.</summary>
    public List<Guid> ReleasedStorageReservations { get; } = [];

    /// <summary>
    /// Il gestionale revoca la prenotazione di una missione: la cella se l'e' presa qualcun altro, o
    /// l'assegnazione e' stata cancellata da un'altra strada. Da qui in poi
    /// <see cref="GetStorageReservationAsync"/> risponde «non c'e'» e il motore riprenota.
    /// </summary>
    public void RevokeStorageReservation(Guid missionId) => StorageReservationsByMission.Remove(missionId);

    /// <summary>
    /// La topologia per magazzino. Un magazzino non preparato risponde una topologia <b>vuota</b> -
    /// nessuna area automatica, nessuna cella - e non un'eccezione come fa <see cref="Cells"/>:
    /// dal compito 10 <c>LocalQueries.GetAutomaticAreasByWarehouseId</c> la chiede lungo ogni
    /// ricerca di deposito, e le aree ammesse non sono un ingresso della maggior parte dei
    /// collaudi che ci passano (il gestionale finto risponde comunque la cella che il test ha
    /// preparato). E' la stessa scelta gia' fatta per <see cref="PickingBufferAreas"/> e
    /// <see cref="CellContents"/>, ed e' un dato di dominio: un magazzino senza aree automatiche.
    /// </summary>
    public Dictionary<decimal, AreaTopology> Topologies { get; } = [];
    public Dictionary<decimal, LoadUnitSearchResult> LoadUnitSearchResults { get; } = [];
    public Dictionary<decimal, PickUpDoorChoice> PickUpDoorChoices { get; } = [];

    /// <summary>
    /// Le porte di prelievo chieste, con <b>tutt'e due</b> le chiavi (compito 20): l'unita' di
    /// carico e la cella. La cella serve a provare che quella trovata dalla ricerca per referenza
    /// arriva fin qui - <see cref="PickUpDoorChoices"/> e' indicizzata per sola unita' di carico e
    /// non la vedrebbe.
    /// </summary>
    public List<ChoosePickUpDoorRequest> PickUpDoorChoiceRequests { get; } = [];

    /// <summary>
    /// Che cosa risponde <see cref="FindLoadUnitByReferenceAsync"/>, per referenza cercata
    /// (compito 20). Una referenza che non e' qui dentro risponde
    /// <see cref="ReferenceSearchOutcome.NotFound"/>: e' il caso piu' comune e non merita una
    /// riga di preparazione in ogni test.
    /// </summary>
    public Dictionary<string, ReferenceSearchResult> ReferenceSearchResults { get; } = [];

    /// <summary>Le ricerche per referenza chieste, nell'ordine (compito 20).</summary>
    public List<FindLoadUnitByReferenceRequest> ReferenceSearches { get; } = [];
    public Dictionary<decimal, CellPartitionMap> CellPartitionMaps { get; } = [];
    public Dictionary<string, List<CellDescription>> CellsByDoor { get; } = [];
    public Dictionary<decimal, List<decimal>> CellContents { get; } = [];

    /// <summary>
    /// Le posizioni di ciascuna cella con il loro stato di prenotazione (compito 18): la memoria di
    /// <see cref="GetCellPositionsAsync"/>, tenuta a parte da <see cref="CellContents"/> perché le
    /// due letture rispondono cose diverse - questa tutte le posizioni, quella le sole non prenotate.
    /// </summary>
    public Dictionary<decimal, List<CellPosition>> CellPositions { get; } = [];

    /// <summary>Le celle rimaste senza posizioni per mano del motore, nell'ordine (compito 18).</summary>
    public List<decimal> ClearedCells { get; } = [];

    public Dictionary<decimal, CellBusyState> CellBusyStates { get; } = [];
    public Dictionary<decimal, CellStoringConstraints> CellStoringConstraintsByCell { get; } = [];
    public List<(string DoorCode, CellLockState LockState)> CellLockStateChanges { get; } = [];
    public IReadOnlyList<PendingNote> PendingNoteWork { get; set; } = [];

    /// <summary>I valori di <c>limitOneShippingNotePerBuffer</c> ricevuti, nell'ordine: l'opzione sta nella configurazione dell'host del WCS e viaggia nella richiesta.</summary>
    public List<bool> PendingNoteWorkLimits { get; } = [];

    private readonly TaskCompletionSource _pendingNoteWorkAsked =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Si completa alla <b>prima</b> chiamata di <see cref="GetPendingNoteWorkAsync"/>: e' il modo
    /// per un test di sapere che il codice sotto prova e' arrivato a quel punto senza discriminare
    /// con un'attesa a tempo, che sotto carico scade su codice sano e produce un rosso falso.
    /// Scatta <b>prima</b> della risposta, quindi anche quando la risposta e' un'eccezione
    /// (<see cref="Unavailable"/>).
    /// </summary>
    public Task PendingNoteWorkAsked => _pendingNoteWorkAsked.Task;

    /// <summary>Le assegnazioni di nota scartate dal motore, con il motivo, nell'ordine.</summary>
    public List<RejectNoteAssignmentRequest> RejectedNoteAssignments { get; } = [];

    /// <summary>
    /// Le segnalazioni «questa unita' di carico ha lasciato la coda di estrazione», nell'ordine.
    /// Lista a parte da <see cref="RejectedNoteAssignments"/>: la chiave e' l'unita' di carico, e
    /// l'assegnazione e' un campo del fatto, non la sua chiave.
    /// </summary>
    public List<LoadUnitLeftExtractionQueueRequest> LoadUnitsLeftExtractionQueue { get; } = [];

    /// <summary>
    /// Le missioni uscite dal motore senza servire l'assegnazione che portavano, nell'ordine
    /// (compito 21).
    /// </summary>
    public List<MissionLeftAssignmentUnservedRequest> MissionsLeftAssignmentUnserved { get; } = [];

    /// <summary>
    /// Le assegnazioni servite da una missione arrivata a destinazione, nell'ordine (compito 21).
    /// Lista a parte dalla precedente: il fatto e' l'opposto, e un test che le confondesse non
    /// vedrebbe la differenza.
    /// </summary>
    public List<MissionServedAssignmentRequest> MissionsServedAssignment { get; } = [];

    /// <summary>Le righe di coda che il motore non ha potuto servire, con il motivo, nell'ordine (compito 21).</summary>
    public List<UnservableQueuedExtractionRequest> UnservableQueuedExtractions { get; } = [];

    /// <summary>Le conferme delle assegnazioni servite, nell'ordine (compito 19, terza operazione).</summary>
    public List<ConfirmServedAssignmentsRequest> ConfirmedServedAssignments { get; } = [];

    /// <summary>
    /// Le estrazioni gia' prelevate diventate un deposito, nell'ordine (voce 45). Come le altre
    /// segnalazioni, una richiesta finisce qui solo se il gestionale ha risposto: quella a cui ha
    /// risposto con un guasto sta solo in <see cref="Calls"/>.
    /// </summary>
    public List<PickedExtractionBecameDepositRequest> PickedExtractionsBecameDeposit { get; } = [];

    /// <summary>
    /// Che cosa risponde il gestionale a <see cref="ReportPickedExtractionBecameDepositAsync"/>, per
    /// richiesta: serve a provare ciascuno dei tre esiti. Per default risponde come il gestionale
    /// vero quando l'ordine e' ancora aperto: riapre quello che la missione aveva servito
    /// (<see cref="PickedExtractionReopenOutcome.Reopened"/>) e risponde
    /// <see cref="PickedExtractionReopenOutcome.NothingToReopen"/> per quello che non aveva servito
    /// (assegnazione 0, riga di estrazione 0). Il gestionale muto si chiede con
    /// <see cref="Unavailable"/> o <see cref="UnavailableOperations"/>, il rifiuto con <see cref="Fault"/>.
    /// </summary>
    public Func<PickedExtractionBecameDepositRequest, PickedExtractionBecameDepositResult> PickedExtractionBecameDepositAnswer { get; set; } =
        request => new PickedExtractionBecameDepositResult(
            request.AssignmentId > 0 ? PickedExtractionReopenOutcome.Reopened : PickedExtractionReopenOutcome.NothingToReopen,
            request.ExtractionRowId > 0 ? PickedExtractionReopenOutcome.Reopened : PickedExtractionReopenOutcome.NothingToReopen,
            null);

    /// <summary>Le testate di estrazione per unita' di carico pronte (compito 15).</summary>
    public IReadOnlyList<PendingExtraction> PendingExtractionWork { get; set; } = [];

    /// <summary>Gli esiti riferiti dal motore, una voce per testata, nell'ordine.</summary>
    public List<ReportExtractionOutcomesRequest> ReportedExtractionOutcomes { get; } = [];

    private readonly TaskCompletionSource _reportExtractionOutcomesAsked =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Si completa alla prima chiamata di <see cref="ReportExtractionOutcomesAsync"/>: permette ai
    /// test concorrenti di fermare il confine senza sostituirne il comportamento reale.
    /// </summary>
    public Task ReportExtractionOutcomesAsked => _reportExtractionOutcomesAsked.Task;

    /// <summary>
    /// Se valorizzato, la segnalazione degli esiti resta ferma finche' il test non apre il varco.
    /// La risposta normale del fake resta quella di sempre dopo l'attesa.
    /// </summary>
    public TaskCompletionSource? ReportExtractionOutcomesGate { get; set; }

    /// <summary>
    /// Le unita' di carico su cui <see cref="FindLoadUnitAsync"/> e <see cref="ChoosePickUpDoorAsync"/>
    /// sollevano <see cref="WarehouseUnavailableException"/> invece di rispondere: serve a provare
    /// che l'indisponibilita' del gestionale su una riga la marca <c>NotReachable</c> e lascia
    /// proseguire le altre, invece di fermare l'intero giro.
    /// </summary>
    public HashSet<decimal> UnavailableOnLoadUnits { get; } = [];

    /// <summary>
    /// Le testate su cui <see cref="ReportExtractionOutcomesAsync"/> solleva: serve a provare che il
    /// guasto su una testata non porta via le testate seguenti.
    /// </summary>
    public HashSet<decimal> UnavailableOnExtractionHeaders { get; } = [];

    /// <summary>
    /// Le righe di estrazione che il gestionale RIFIUTA: una richiesta che ne contiene anche una sola
    /// torna un 400, cioe' una <see cref="WarehouseUnavailableException"/> con
    /// <see cref="WarehouseUnavailableException.IsPermanentRejection"/> vero - un no, non un'assenza.
    /// </summary>
    public HashSet<decimal> RejectedExtractionRows { get; } = [];

    /// <summary>Le unita' di carico su cui allocazione e rilascio tornano un 400: lo stesso rifiuto, per le transizioni.</summary>
    public HashSet<decimal> RejectedTransitionLoadUnits { get; } = [];

    /// <summary>Un rifiuto come lo solleva il client vero: il 4xx viaggia nell'eccezione interna.</summary>
    public static WarehouseUnavailableException Rejection(string operation) =>
        new(operation, new HttpRequestException("rifiutata", null, System.Net.HttpStatusCode.BadRequest));

    /// <summary>Le unita' di carico cercate con <see cref="FindLoadUnitAsync"/>, nell'ordine.</summary>
    public List<decimal> FoundLoadUnitIds { get; } = [];

    /// <summary>
    /// Le richieste intere di <see cref="FindLoadUnitAsync"/>, nell'ordine, come
    /// <see cref="ReferenceSearches"/> fa per la ricerca per referenza: <see cref="FoundLoadUnitIds"/>
    /// dice <b>quale</b> unita' di carico si e' cercata, questa anche <b>dove</b> - le aree ammesse e
    /// il loro ordine, che e' il dato del compito 2.
    /// </summary>
    public List<FindLoadUnitRequest> LoadUnitSearches { get; } = [];
    public Dictionary<decimal, LoadUnitDescription> LoadUnits { get; } = [];
    public HashSet<string> StoredLoadUnitCodes { get; } = [];
    public Dictionary<string, decimal> LoadUnitIdsByCode { get; } = [];
    public List<AllocateLoadUnitRequest> AllocatedLoadUnits { get; } = [];
    public List<ReleaseLoadUnitRequest> ReleasedLoadUnits { get; } = [];

    /// <summary>
    /// Le operazioni che questo gestionale non serve, per nome (<c>nameof</c>): come
    /// <see cref="Unavailable"/>, ma su una domanda sola. Serve quando un metodo sotto prova ne fa
    /// piu' d'una in fila e si vuole far fallire <b>proprio quella</b> che il test guarda: con
    /// <see cref="Unavailable"/> solleverebbe la prima, e il test proverebbe un'altra strada.
    /// </summary>
    public HashSet<string> UnavailableOperations { get; } = [];

    /// <summary>
    /// Chiamato da ogni operazione che passa da <c>Answer</c>, nel momento in cui il gestionale la
    /// riceve e prima che risponda: se restituisce un'eccezione, l'operazione la solleva al posto
    /// della risposta. Serve a due cose che <see cref="UnavailableOperations"/> non fa: guardare lo
    /// stato del WCS nell'istante della chiamata, e far fallire una chiamata precisa (la seconda
    /// dello stesso nome) con il guasto che il client vero solleverebbe - una connessione rifiutata,
    /// un 4xx - invece dell'eccezione nuda. Nullo per default.
    /// </summary>
    public Func<string, Exception?>? Fault { get; set; }

    private T Answer<T>(string operation, Func<T> value)
    {
        Calls.Add(operation);
        if (Fault?.Invoke(operation) is { } fault) throw fault;
        if (Unavailable || UnavailableOperations.Contains(operation)) throw new WarehouseUnavailableException(operation);
        return value();
    }

    private void Answer(string operation)
    {
        Calls.Add(operation);
        if (Fault?.Invoke(operation) is { } fault) throw fault;
        if (Unavailable || UnavailableOperations.Contains(operation)) throw new WarehouseUnavailableException(operation);
    }

    /// <summary>
    /// Le celle su cui <see cref="DescribeCellAsync"/> e <see cref="GetCellPartitionMapAsync"/>
    /// sollevano <see cref="WarehouseUnavailableException"/> invece di rispondere, come
    /// <see cref="UnavailableOnLoadUnits"/> fa per le unita' di carico: serve a far fallire la sola
    /// lettura dell'indirizzo di campo, lasciando rispondere tutte le altre - e' quello che accadde
    /// il 2026-09-09, quando la mappa delle partizioni della cella 529.0 rispose 404 in un giro in
    /// cui il resto del gestionale rispondeva.
    /// </summary>
    public HashSet<decimal> UnavailableOnCells { get; } = [];

    public async Task<CellDescription> DescribeCellAsync(decimal cellId, CancellationToken ct = default)
    {
        // Stesso ritardo per nome di AllocateLoadUnitAsync: e' la lettura dell'indirizzo di campo
        // che il movimento raggruppato fa dopo aver preso (o no) il posto sulle destinazioni.
        if (Before != null) await Before(nameof(DescribeCellAsync));

        return Answer(nameof(DescribeCellAsync), () =>
        {
            if (UnavailableOnCells.Contains(cellId))
                throw new WarehouseUnavailableException(nameof(DescribeCellAsync));

            return Cells[cellId];
        });
    }

    /// <summary>
    /// Si completa alla <b>prima</b> chiamata di <see cref="ReserveStorageAsync"/>, come
    /// <see cref="PendingNoteWorkAsked"/>: e' il modo per un test di sapere che la ricerca della
    /// destinazione e' arrivata al gestionale senza discriminare con un'attesa a tempo. Scatta
    /// <b>prima</b> di <see cref="ReserveStorageGate"/>, cosi' il test puo' aspettare l'arrivo e poi
    /// decidere quando far rispondere.
    /// <para>
    /// <b>Solo la prima:</b> dalla seconda chiamata in poi il gettone e' gia' completo, quindi un
    /// test che lo aspettasse per osservare una <b>seconda</b> ricerca passerebbe subito senza avere
    /// osservato niente. Per contare piu' ricerche si guarda <see cref="Calls"/>; per fermare la
    /// seconda serve un gettone nuovo, che oggi non c'e'.
    /// </para>
    /// </summary>
    public Task ReserveStorageAsked => _reserveStorageAsked.Task;

    private readonly TaskCompletionSource _reserveStorageAsked =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Se valorizzato, <see cref="ReserveStorageAsync"/> non risponde finche' il test non lo
    /// completa: e' il giro di rete lento del confine, tenuto fermo dal test invece che da un
    /// ritardo. Non impostato, la risposta e' immediata come per ogni altra lettura.
    /// </summary>
    public TaskCompletionSource? ReserveStorageGate { get; set; }

    /// <summary>
    /// Le richieste intere di <see cref="ReserveStorageAsync"/>, nell'ordine, come
    /// <see cref="LoadUnitSearches"/> per la ricerca delle unita' di carico: la risposta qui non
    /// dipende dalla richiesta (e' <see cref="StorageReservations"/> per unita' di carico), e quello
    /// che si chiede - per esempio le celle escluse - si guarda da qui.
    /// </summary>
    public List<ReserveStorageRequest> StorageRequests { get; } = [];

    public async Task<StorageReservation> ReserveStorageAsync(ReserveStorageRequest request, CancellationToken ct = default)
    {
        StorageRequests.Add(request);
        _reserveStorageAsked.TrySetResult();

        if (ReserveStorageGate is { } gate) await gate.Task.WaitAsync(ct);
        if (Before != null) await Before(nameof(ReserveStorageAsync));

        return Answer(nameof(ReserveStorageAsync), () =>
        {
            var reservation = StorageReservations[request.LoadUnitId];

            // Come il gestionale (compito 17): il legame con la missione nasce solo quando la
            // prenotazione ha trovato davvero una cella, c'e' una missione a cui appartenere - una
            // richiesta senza missione (MoveObstacleLu, che prenota prima che la missione esista)
            // non lascia nessun legame - e la posizione e' stata davvero scritta (con
            // AssignPosition falso e' la sola verifica di disponibilita', che non tiene niente).
            if (reservation.Outcome == StorageReservationOutcome.CellFound && request.AssignPosition)
            {
                // Come POSIZIONI_UDC: una nuova assegnazione della stessa UDC sostituisce anche quella
                // nata senza legame di missione (ostacolo, ricarica).
                LoadUnitCellsHeld.Remove(request.LoadUnitId);
                if (request.MissionId == Guid.Empty)
                    LoadUnitCellsHeld[request.LoadUnitId] = reservation.CellId;
                else
                    StorageReservationsByMission[request.MissionId] = reservation with { MissionId = request.MissionId };
            }

            return reservation;
        });
    }

    public Task<StorageReservation?> GetStorageReservationAsync(Guid missionId, CancellationToken ct = default) =>
        Task.FromResult(Answer(nameof(GetStorageReservationAsync),
            () => StorageReservationsByMission.GetValueOrDefault(missionId)));

    public Task ReleaseStorageReservationAsync(Guid missionId, CancellationToken ct = default)
    {
        // Come FindLoadUnitAsync: senza un ritardo davanti, la risposta di sempre.
        if (Before is not { } before)
        {
            Release();
            return Task.CompletedTask;
        }

        return AfterBefore();

        async Task AfterBefore()
        {
            await before(nameof(ReleaseStorageReservationAsync));
            Release();
        }

        void Release()
        {
            Answer(nameof(ReleaseStorageReservationAsync));
            ReleasedStorageReservations.Add(missionId);
            StorageReservationsByMission.Remove(missionId);
        }
    }

    /// <summary>
    /// The cells the warehouse holds for a load unit outside a mission's link (item 54): the
    /// reservations of the changes of cell born in the engine. <see cref="ReleaseLoadUnitCellAsync"/>
    /// releases a cell held here, or held by the request's mission in
    /// <see cref="StorageReservationsByMission"/>, and answers <see cref="LoadUnitCellReleaseOutcome.NotHeld"/>
    /// otherwise.
    /// </summary>
    public Dictionary<decimal, decimal> LoadUnitCellsHeld { get; } = [];

    /// <summary>The cells the engine gave back by load unit and cell (item 54), in order, whatever the outcome.</summary>
    public List<ReleaseLoadUnitCellRequest> ReleasedLoadUnitCells { get; } = [];

    public Task<LoadUnitCellReleaseOutcome> ReleaseLoadUnitCellAsync(ReleaseLoadUnitCellRequest request, CancellationToken ct = default)
    {
        var outcome = Answer(nameof(ReleaseLoadUnitCellAsync), () =>
        {
            var released = false;
            if (LoadUnitCellsHeld.TryGetValue(request.LoadUnitId, out var held) && held == request.CellId)
                released = LoadUnitCellsHeld.Remove(request.LoadUnitId);

            if (StorageReservationsByMission.TryGetValue(request.MissionId, out var reservation) &&
                reservation.CellId == request.CellId)
                released = StorageReservationsByMission.Remove(request.MissionId) || released;

            return released ? LoadUnitCellReleaseOutcome.Released : LoadUnitCellReleaseOutcome.NotHeld;
        });
        ReleasedLoadUnitCells.Add(request);
        return Task.FromResult(outcome);
    }

    /// <summary>
    /// The candidate cells per load unit (item 54), in the warehouse's order:
    /// <see cref="ListStorageCellCandidatesAsync"/> answers the first <see cref="StorageCellCandidatesRequest.Limit"/>,
    /// whatever the rest of the request says - what was asked is in <see cref="StorageCellCandidateRequests"/>.
    /// </summary>
    public Dictionary<decimal, List<StorageCellCandidate>> StorageCellCandidates { get; } = [];

    /// <summary>The requests of <see cref="ListStorageCellCandidatesAsync"/>, in order.</summary>
    public List<StorageCellCandidatesRequest> StorageCellCandidateRequests { get; } = [];

    /// <summary>
    /// The cells <see cref="ReserveStorageCellAsync"/> reserves, by door A code (item 54): a door found here
    /// is free and suited, any other door is unknown. A reserved cell is the mission's reservation in
    /// <see cref="StorageReservationsByMission"/>, or without a mission the load unit's in
    /// <see cref="LoadUnitCellsHeld"/>, as the warehouse holds it.
    /// </summary>
    public Dictionary<string, StorageReservation> ReservableStorageCells { get; } = [];

    /// <summary>The requests of <see cref="ReserveStorageCellAsync"/>, in order, whatever the outcome.</summary>
    public List<ReserveStorageCellRequest> StorageCellReservationRequests { get; } = [];

    /// <summary>
    /// If set and not answering <see langword="null"/>, the answer of <see cref="ReserveStorageCellAsync"/>
    /// instead of <see cref="ReservableStorageCells"/>: for the refusals (door B, cell not available) a test
    /// wants to see. Nothing is held by an answer from here.
    /// </summary>
    public Func<ReserveStorageCellRequest, StorageCellReservationResult?>? ReserveStorageCellAnswer { get; set; }

    public Task<IReadOnlyList<StorageCellCandidate>> ListStorageCellCandidatesAsync(StorageCellCandidatesRequest request, CancellationToken ct = default)
    {
        StorageCellCandidateRequests.Add(request);
        return Task.FromResult(Answer<IReadOnlyList<StorageCellCandidate>>(nameof(ListStorageCellCandidatesAsync), () =>
            [.. StorageCellCandidates.GetValueOrDefault(request.LoadUnitId, []).Take(StorageCellCandidatesRequest.Limit)]));
    }

    /// <summary>Whether <see cref="CheckStorageCellAsync"/> is offered (item 55): false answers null, as a warehouse without the route.</summary>
    public bool OffersStorageCellCheck { get; set; } = true;

    /// <summary>The requests of <see cref="CheckStorageCellAsync"/>, in order.</summary>
    public List<ReserveStorageCellRequest> StorageCellCheckRequests { get; } = [];

    /// <summary>
    /// What <see cref="ReserveStorageCellAsync"/> would answer, holding nothing: the refusal of
    /// <see cref="ReserveStorageCellAnswer"/>, otherwise «it would» for a door of
    /// <see cref="ReservableStorageCells"/> and «unknown door» for any other.
    /// </summary>
    public Task<StorageCellReservationResult?> CheckStorageCellAsync(ReserveStorageCellRequest request, CancellationToken ct = default)
    {
        StorageCellCheckRequests.Add(request);
        return Task.FromResult(Answer<StorageCellReservationResult?>(nameof(CheckStorageCellAsync), () =>
        {
            if (!OffersStorageCellCheck) return null;
            if (ReserveStorageCellAnswer?.Invoke(request) is { } answer) return answer with { Reservation = null };
            return ReservableStorageCells.ContainsKey(request.DoorCode)
                ? new StorageCellReservationResult(StorageCellReservationOutcome.Reserved, null, null)
                : new StorageCellReservationResult(StorageCellReservationOutcome.UnknownDoor, null,
                    $"no cell has the door {request.DoorCode}");
        }));
    }

    public Task<StorageCellReservationResult> ReserveStorageCellAsync(ReserveStorageCellRequest request, CancellationToken ct = default)
    {
        StorageCellReservationRequests.Add(request);
        return Task.FromResult(Answer(nameof(ReserveStorageCellAsync), () =>
        {
            if (ReserveStorageCellAnswer?.Invoke(request) is { } answer) return answer;

            if (!ReservableStorageCells.TryGetValue(request.DoorCode, out var cell))
                return new StorageCellReservationResult(StorageCellReservationOutcome.UnknownDoor, null,
                    $"no cell has the door {request.DoorCode}");

            var reservation = cell with { MissionId = request.MissionId };
            // La posizione assegnata e' una sola per UDC: la cella precisa sostituisce anche una
            // prenotazione precedente nata nel motore e quindi priva del legame con la missione.
            LoadUnitCellsHeld.Remove(request.LoadUnitId);
            if (request.MissionId == Guid.Empty)
                LoadUnitCellsHeld[request.LoadUnitId] = reservation.CellId;
            else
                StorageReservationsByMission[request.MissionId] = reservation;

            return new StorageCellReservationResult(StorageCellReservationOutcome.Reserved, reservation, null);
        }));
    }

    public Task<LoadUnitLocation?> LocateLoadUnitAsync(decimal loadUnitId, CancellationToken ct = default) =>
        Task.FromResult(Answer(nameof(LocateLoadUnitAsync), () => LoadUnitLocations.GetValueOrDefault(loadUnitId)));

    public Task<IReadOnlyList<PendingNote>> GetPendingNoteWorkAsync(bool limitOneShippingNotePerBuffer, CancellationToken ct = default)
    {
        PendingNoteWorkLimits.Add(limitOneShippingNotePerBuffer);
        _pendingNoteWorkAsked.TrySetResult();
        return Task.FromResult(Answer(nameof(GetPendingNoteWorkAsync), () => PendingNoteWork));
    }

    public Task RejectNoteAssignmentAsync(RejectNoteAssignmentRequest request, CancellationToken ct = default)
    {
        Answer(nameof(RejectNoteAssignmentAsync));
        RejectedNoteAssignments.Add(request);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Le assegnazioni di nota la cui lavorazione non e' piu' in corso (compito 6): come la
    /// gemella <see cref="PausedExtractionRows"/>, un insieme, perche' la risposta e' la presenza.
    /// Un'assegnazione che non c'e' dentro non e' in pausa - e' un dato, non un'assenza da
    /// segnalare: il guasto si chiede con <see cref="Unavailable"/> o con
    /// <see cref="UnavailableOperations"/>.
    /// </summary>
    public HashSet<decimal> PausedNoteAssignments { get; } = [];

    /// <summary>
    /// Gli elenchi su cui <see cref="GetPausedNoteAssignmentsAsync"/> e' stata interrogata,
    /// nell'ordine. Serve a dire <b>che cosa</b> si e' chiesto: qui la domanda parte dalla coda del
    /// WCS, e il difetto che il compito 6 chiude era proprio una domanda senza chiave - tutta
    /// <c>ASSEGNAZIONE_NOTE</c> - che avrebbe risposto lo stesso qualcosa di plausibile.
    /// </summary>
    public List<IReadOnlyList<decimal>> PausedNoteAssignmentsAsked { get; } = [];

    /// <summary>The assignments that no longer exist (item D119).</summary>
    public HashSet<decimal> MissingNoteAssignments { get; } = [];

    /// <summary>Whether <see cref="GetMissingNoteAssignmentsAsync"/> is offered: false answers null, as a warehouse without the route.</summary>
    public bool OffersMissingNoteAssignments { get; set; } = true;

    public Task<IReadOnlyList<decimal>?> GetMissingNoteAssignmentsAsync(IReadOnlyList<decimal> assignmentIds, CancellationToken ct = default)
    {
        if (assignmentIds.Count == 0) return Task.FromResult<IReadOnlyList<decimal>?>([]);
        return Task.FromResult(Answer<IReadOnlyList<decimal>?>(nameof(GetMissingNoteAssignmentsAsync), () =>
            OffersMissingNoteAssignments ? [.. assignmentIds.Distinct().Where(MissingNoteAssignments.Contains)] : null));
    }

    public Task<IReadOnlyList<decimal>> GetPausedNoteAssignmentsAsync(IReadOnlyList<decimal> assignmentIds, CancellationToken ct = default)
    {
        // Come la gemella: la scorciatoia dell'elenco vuoto e' del contratto, e qui non si registra
        // nemmeno la chiamata, cosi' un test puo' provare che il confine non e' stato toccato.
        if (assignmentIds.Count == 0) return Task.FromResult<IReadOnlyList<decimal>>([]);

        PausedNoteAssignmentsAsked.Add(assignmentIds);

        return Task.FromResult(Answer(nameof(GetPausedNoteAssignmentsAsync),
            () => (IReadOnlyList<decimal>)assignmentIds.Where(PausedNoteAssignments.Contains).ToList()));
    }

    public Task ReportLoadUnitLeftExtractionQueueAsync(LoadUnitLeftExtractionQueueRequest request, CancellationToken ct = default)
    {
        Answer(nameof(ReportLoadUnitLeftExtractionQueueAsync));
        LoadUnitsLeftExtractionQueue.Add(request);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PendingExtraction>> GetPendingExtractionWorkAsync(CancellationToken ct = default) =>
        Task.FromResult(Answer(nameof(GetPendingExtractionWorkAsync), () => PendingExtractionWork));

    /// <summary>
    /// Le righe di estrazione la cui lavorazione non e' piu' in corso (compito 5): un insieme e non
    /// un dizionario, perche' la risposta e' la presenza. Una riga che non c'e' dentro non e' in
    /// pausa - e' un dato, non un'assenza da segnalare: il guasto si chiede con
    /// <see cref="Unavailable"/> o con <see cref="UnavailableOperations"/>.
    /// </summary>
    public HashSet<decimal> PausedExtractionRows { get; } = [];

    /// <summary>
    /// Gli elenchi su cui <see cref="GetPausedExtractionRowsAsync"/> e' stata interrogata, nell'ordine:
    /// un test guarda <b>che cosa</b> si e' chiesto, non solo che cosa si e' ricevuto - qui la
    /// domanda parte dalla coda del WCS, e chiedere gli identificativi sbagliati risponderebbe lo
    /// stesso una lista plausibile. La gemella delle note ha il suo elenco
    /// (<see cref="PausedNoteAssignmentsAsked"/>): sono due operazioni, e un test deve poter dire
    /// quale delle due e' stata fatta.
    /// </summary>
    public List<IReadOnlyList<decimal>> PausedExtractionRowsAsked { get; } = [];

    public Task<IReadOnlyList<decimal>> GetPausedExtractionRowsAsync(IReadOnlyList<decimal> extractionRowIds, CancellationToken ct = default)
    {
        // La scorciatoia dell'elenco vuoto e' del contratto, non del gestionale finto: qui non si
        // registra nemmeno la chiamata, cosi' un test puo' provare che il confine non e' stato
        // toccato. La fa anche HttpWarehouse, e per lo stesso motivo.
        if (extractionRowIds.Count == 0) return Task.FromResult<IReadOnlyList<decimal>>([]);

        PausedExtractionRowsAsked.Add(extractionRowIds);

        return Task.FromResult(Answer(nameof(GetPausedExtractionRowsAsync),
            () => (IReadOnlyList<decimal>)extractionRowIds.Where(PausedExtractionRows.Contains).ToList()));
    }

    public async Task ReportExtractionOutcomesAsync(ReportExtractionOutcomesRequest request, CancellationToken ct = default)
    {
        _reportExtractionOutcomesAsked.TrySetResult();

        if (ReportExtractionOutcomesGate is { } gate) await gate.Task.WaitAsync(ct);

        Answer(nameof(ReportExtractionOutcomesAsync));

        if (UnavailableOnExtractionHeaders.Contains(request.ExtractionHeaderId))
            throw new WarehouseUnavailableException(nameof(ReportExtractionOutcomesAsync));

        if (request.Outcomes.Any(outcome => RejectedExtractionRows.Contains(outcome.ExtractionRowId)))
            throw Rejection(nameof(ReportExtractionOutcomesAsync));

        ReportedExtractionOutcomes.Add(request);
    }

    public Task ReportMissionLeftAssignmentUnservedAsync(MissionLeftAssignmentUnservedRequest request, CancellationToken ct = default)
    {
        Answer(nameof(ReportMissionLeftAssignmentUnservedAsync));
        MissionsLeftAssignmentUnserved.Add(request);
        return Task.CompletedTask;
    }

    public Task ReportMissionServedAssignmentAsync(MissionServedAssignmentRequest request, CancellationToken ct = default)
    {
        Answer(nameof(ReportMissionServedAssignmentAsync));
        MissionsServedAssignment.Add(request);
        return Task.CompletedTask;
    }

    public Task ReportUnservableQueuedExtractionAsync(UnservableQueuedExtractionRequest request, CancellationToken ct = default)
    {
        Answer(nameof(ReportUnservableQueuedExtractionAsync));
        UnservableQueuedExtractions.Add(request);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Si completa alla <b>prima</b> chiamata di <see cref="ConfirmServedAssignmentsAsync"/>, con la
    /// stessa regola di <see cref="ReserveStorageAsked"/>: la consegna del prelievo e' arrivata alla
    /// conferma diretta. Scatta <b>prima</b> di <see cref="ConfirmServedAssignmentsGate"/>.
    /// </summary>
    public Task ConfirmServedAssignmentsAsked => _confirmServedAssignmentsAsked.Task;

    private readonly TaskCompletionSource _confirmServedAssignmentsAsked =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Se valorizzato, <see cref="ConfirmServedAssignmentsAsync"/> non risponde finche' il test non lo
    /// completa: la conferma diretta del prelievo in volo, tenuta ferma dal test invece che da un ritardo.
    /// </summary>
    public TaskCompletionSource? ConfirmServedAssignmentsGate { get; set; }

    public async Task ConfirmServedAssignmentsAsync(ConfirmServedAssignmentsRequest request, CancellationToken ct = default)
    {
        _confirmServedAssignmentsAsked.TrySetResult();

        if (ConfirmServedAssignmentsGate is { } gate) await gate.Task.WaitAsync(ct);

        Answer(nameof(ConfirmServedAssignmentsAsync));
        ConfirmedServedAssignments.Add(request);
    }

    public Task<PickedExtractionBecameDepositResult> ReportPickedExtractionBecameDepositAsync(PickedExtractionBecameDepositRequest request, CancellationToken ct = default)
    {
        var result = Answer(nameof(ReportPickedExtractionBecameDepositAsync), () => PickedExtractionBecameDepositAnswer(request));
        PickedExtractionsBecameDeposit.Add(request);
        return Task.FromResult(result);
    }

    /// <summary>
    /// Un ritardo da mettere davanti a un'operazione, per nome: serve ai test che provano un
    /// <b>ordine</b> fra due chiamate al confine (chi deve aspettare chi), perche' un doppio che
    /// risponde all'istante non fa mai vedere la corsa. Vuoto per default: nessuna attesa.
    /// </summary>
    public Func<string, Task>? Before { get; set; }

    public async Task AllocateLoadUnitAsync(AllocateLoadUnitRequest request, CancellationToken ct = default)
    {
        if (Before != null) await Before(nameof(AllocateLoadUnitAsync));
        Answer(nameof(AllocateLoadUnitAsync));
        if (RejectedTransitionLoadUnits.Contains(request.LoadUnitId)) throw Rejection(nameof(AllocateLoadUnitAsync));
        AllocatedLoadUnits.Add(request);
    }

    public Task ReleaseLoadUnitAsync(ReleaseLoadUnitRequest request, CancellationToken ct = default)
    {
        Answer(nameof(ReleaseLoadUnitAsync));
        if (RejectedTransitionLoadUnits.Contains(request.LoadUnitId)) throw Rejection(nameof(ReleaseLoadUnitAsync));
        ReleasedLoadUnits.Add(request);
        return Task.CompletedTask;
    }

    // Il magazzino non preparato risponde una topologia vuota, non un'eccezione: chi vuole
    // sorvegliare quali aree arrivino al gestionale (le SearchAreaIds della richiesta di deposito)
    // deve quindi preparare Topologies, altrimenti l'assenza risponde [] e la prova passa per il
    // motivo sbagliato. Quando le aree contano, la topologia si mette esplicitamente.
    public Task<AreaTopology> GetAreaTopologyAsync(decimal warehouseId, CancellationToken ct = default) =>
        Task.FromResult(Answer(nameof(GetAreaTopologyAsync),
            () => Topologies.GetValueOrDefault(warehouseId,
                new AreaTopology(warehouseId, [], new Dictionary<decimal, decimal>()))));

    public Task<LoadUnitSearchResult?> FindLoadUnitAsync(FindLoadUnitRequest request, CancellationToken ct = default)
    {
        // Senza un ritardo da mettere davanti, la risposta e' quella di sempre, anche nel modo: un
        // gestionale fermo solleva subito, non in un Task.
        return Before is { } before ? AfterBefore(before) : Task.FromResult(FindLoadUnit());

        async Task<LoadUnitSearchResult?> AfterBefore(Func<string, Task> delay)
        {
            await delay(nameof(FindLoadUnitAsync));
            return FindLoadUnit();
        }

        LoadUnitSearchResult? FindLoadUnit() => Answer(nameof(FindLoadUnitAsync), () =>
        {
            FoundLoadUnitIds.Add(request.LoadUnitId);
            LoadUnitSearches.Add(request);

            if (UnavailableOnLoadUnits.Contains(request.LoadUnitId))
                throw new WarehouseUnavailableException(nameof(FindLoadUnitAsync));

            return LoadUnitSearchResults.GetValueOrDefault(request.LoadUnitId);
        });
    }

    public Task<ReferenceSearchResult> FindLoadUnitByReferenceAsync(FindLoadUnitByReferenceRequest request, CancellationToken ct = default) =>
        Task.FromResult(Answer(nameof(FindLoadUnitByReferenceAsync), () =>
        {
            ReferenceSearches.Add(request);

            return ReferenceSearchResults.GetValueOrDefault(request.ReferenceId)
                   ?? new ReferenceSearchResult(ReferenceSearchOutcome.NotFound, 0, 0);
        }));

    public Task<PickUpDoorChoice> ChoosePickUpDoorAsync(decimal loadUnitId, decimal cellId, CancellationToken ct = default) =>
        Task.FromResult(Answer(nameof(ChoosePickUpDoorAsync), () =>
        {
            PickUpDoorChoiceRequests.Add(new ChoosePickUpDoorRequest(loadUnitId, cellId));

            if (UnavailableOnLoadUnits.Contains(loadUnitId))
                throw new WarehouseUnavailableException(nameof(ChoosePickUpDoorAsync));

            return PickUpDoorChoices[loadUnitId];
        }));

    public Task<CellPartitionMap> GetCellPartitionMapAsync(decimal cellId, CancellationToken ct = default) =>
        Task.FromResult(Answer(nameof(GetCellPartitionMapAsync), () =>
        {
            if (UnavailableOnCells.Contains(cellId))
                throw new WarehouseUnavailableException(nameof(GetCellPartitionMapAsync));

            return CellPartitionMaps[cellId];
        }));

    public Task<IReadOnlyList<CellDescription>> DescribeCellsByDoorAsync(string doorCode, CancellationToken ct = default) =>
        Task.FromResult(Answer(nameof(DescribeCellsByDoorAsync),
            () => (IReadOnlyList<CellDescription>)CellsByDoor.GetValueOrDefault(doorCode, [])));

    public Task SetCellLockStateAsync(string doorCode, CellLockState lockState, CancellationToken ct = default)
    {
        Answer(nameof(SetCellLockStateAsync));
        CellLockStateChanges.Add((doorCode, lockState));
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<decimal>> GetCellContentAsync(decimal cellId, CancellationToken ct = default)
    {
        // Stesso ritardo per nome: e' la PRIMA lettura che il movimento raggruppato fa verso il
        // gestionale (la verifica di allocazione, prima dell'indirizzo di campo), ed e' li' che
        // la finestra della voce D152 si apre.
        if (Before != null) await Before(nameof(GetCellContentAsync));

        return Answer(nameof(GetCellContentAsync),
            () => (IReadOnlyList<decimal>)CellContents.GetValueOrDefault(cellId, []));
    }

    /// <summary>
    /// Le aree di prelievo per cella polmone (compito 2). Una cella che non c'e' nel dizionario non
    /// serve nessuna area: lista vuota, che e' un dato - il guasto si chiede con
    /// <see cref="Unavailable"/>, ed e' proprio la distinzione che l'operazione porta.
    /// <b>L'ordine della lista si conserva</b>: e' la priorita', non un insieme.
    /// </summary>
    public Dictionary<decimal, List<decimal>> PickingBufferAreas { get; } = [];

    public Task<IReadOnlyList<decimal>> GetPickingBufferAreasAsync(decimal cellId, CancellationToken ct = default) =>
        Task.FromResult(Answer(nameof(GetPickingBufferAreasAsync),
            () => (IReadOnlyList<decimal>)PickingBufferAreas.GetValueOrDefault(cellId, [])));

    public Task<IReadOnlyList<CellPosition>> GetCellPositionsAsync(decimal cellId, CancellationToken ct = default) =>
        Task.FromResult(Answer(nameof(GetCellPositionsAsync),
            () => (IReadOnlyList<CellPosition>)CellPositions.GetValueOrDefault(cellId, [])));

    public Task ClearCellPositionsAsync(decimal cellId, CancellationToken ct = default)
    {
        Answer(nameof(ClearCellPositionsAsync));
        ClearedCells.Add(cellId);
        CellPositions.Remove(cellId);
        return Task.CompletedTask;
    }

    public Task<CellBusyState> GetCellBusyStateAsync(decimal cellId, decimal loadUnitIdToExclude = 0, CancellationToken ct = default) =>
        Task.FromResult(Answer(nameof(GetCellBusyStateAsync),
            () => CellBusyStates.GetValueOrDefault(cellId, new CellBusyState(false, false, false))));

    public Task<CellStoringConstraints> GetCellStoringConstraintsAsync(decimal cellId, CellDoor door, int loadUnitDepth, decimal loadUnitIdToExclude, CancellationToken ct = default) =>
        Task.FromResult(Answer(nameof(GetCellStoringConstraintsAsync), () => CellStoringConstraintsByCell[cellId]));

    /// <summary>
    /// Si completa alla <b>prima</b> chiamata di <see cref="DescribeLoadUnitAsync"/>, con la stessa
    /// regola di <see cref="ReserveStorageAsked"/>: dice a un test che il codice sotto prova e'
    /// arrivato al confine, senza discriminare con un'attesa. Scatta <b>prima</b> di
    /// <see cref="DescribeLoadUnitGate"/>.
    /// </summary>
    public Task DescribeLoadUnitAsked => _describeLoadUnitAsked.Task;

    private readonly TaskCompletionSource _describeLoadUnitAsked =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Se valorizzato, <see cref="DescribeLoadUnitAsync"/> non risponde finche' il test non lo
    /// completa: e' il giro di rete lento del confine, tenuto fermo dal test invece che da un
    /// ritardo. Serve a distinguere chi <b>attende</b> la risposta da chi e' andato avanti senza.
    /// </summary>
    public TaskCompletionSource? DescribeLoadUnitGate { get; set; }

    public async Task<LoadUnitDescription?> DescribeLoadUnitAsync(decimal loadUnitId, CancellationToken ct = default)
    {
        _describeLoadUnitAsked.TrySetResult();

        if (DescribeLoadUnitGate is { } gate) await gate.Task.WaitAsync(ct);

        return Answer(nameof(DescribeLoadUnitAsync), () => LoadUnits.GetValueOrDefault(loadUnitId));
    }

    /// <summary>
    /// La modalita' di movimentazione per tipo di UDC: chiave il tipo, quindi un dizionario suo e
    /// non <see cref="LoadUnits"/>. Un tipo assente risponde «non c'e'».
    /// </summary>
    public Dictionary<decimal, LoadUnitTypeHandlingMode> LoadUnitTypeHandlingModes { get; } = [];

    public Task<LoadUnitTypeHandlingMode?> GetLoadUnitTypeHandlingModeAsync(decimal loadUnitTypeId, CancellationToken ct = default) =>
        Task.FromResult(Answer(nameof(GetLoadUnitTypeHandlingModeAsync), () => LoadUnitTypeHandlingModes.GetValueOrDefault(loadUnitTypeId)));

    /// <summary>
    /// Le unita' di carico che una nota di spedizione ha impegnato (compito 3): un insieme e non un
    /// dizionario, perche' la risposta e' la presenza. Un'unita' di carico che non c'e' dentro non
    /// e' assegnata - e' un dato, non un'assenza da segnalare: il guasto si chiede con
    /// <see cref="Unavailable"/>.
    /// </summary>
    public HashSet<decimal> LoadUnitsAssignedToNotes { get; } = [];

    public Task<LoadUnitAssignmentState> GetLoadUnitAssignmentStateAsync(decimal loadUnitId, CancellationToken ct = default) =>
        Task.FromResult(Answer(nameof(GetLoadUnitAssignmentStateAsync),
            () => new LoadUnitAssignmentState(loadUnitId, LoadUnitsAssignedToNotes.Contains(loadUnitId))));

    public Task<bool> IsLoadUnitStoredAsync(string? code, CancellationToken ct = default) =>
        Task.FromResult(Answer(nameof(IsLoadUnitStoredAsync), () => !string.IsNullOrEmpty(code) && StoredLoadUnitCodes.Contains(code)));

    public Task<LoadUnitIdentity?> ResolveLoadUnitByCodeAsync(string? code, CancellationToken ct = default) =>
        Task.FromResult(Answer(nameof(ResolveLoadUnitByCodeAsync), () =>
            code != null && LoadUnitIdsByCode.TryGetValue(code, out var id) ? new LoadUnitIdentity(id) : null));

    /// <summary>
    /// I verdetti di deframmentazione, per <b>coppia</b> area piu' tipo di unita' di carico
    /// (compito 4): la stessa area risponde diversamente per due tipi, e una chiave sulla sola area
    /// nasconderebbe proprio l'errore di chi dimenticasse il tipo. Una coppia assente risponde
    /// «non serve» - e' un dato, non un'assenza da segnalare: il guasto si chiede con
    /// <see cref="Unavailable"/> o con <see cref="UnavailableOperations"/>.
    /// </summary>
    public Dictionary<(decimal AreaId, decimal LoadUnitTypeId), bool> DefragmentationVerdicts { get; } = [];

    public Task<AreaDefragmentationVerdict> GetAreaDefragmentationVerdictAsync(decimal areaId, decimal loadUnitTypeId, CancellationToken ct = default) =>
        Task.FromResult(Answer(nameof(GetAreaDefragmentationVerdictAsync),
            () => new AreaDefragmentationVerdict(areaId, loadUnitTypeId,
                DefragmentationVerdicts.GetValueOrDefault((areaId, loadUnitTypeId)))));

    /// <summary>
    /// Le celle che l'elenco dell'Editor riceve (compito 7), <b>tutte</b>: il doppio non filtra
    /// ne' per tipo d'area ne' per «solo automatiche». Il filtro e' del gestionale, e quello che
    /// da questa parte serve provare e' che i due valori arrivino - per questo finiscono in
    /// <see cref="EditorCellQueries"/>, e un filtro perso per strada si vede li'.
    /// </summary>
    public List<EditorCellRow> EditorCells { get; } = [];

    /// <summary>I filtri con cui le celle sono state chieste, nell'ordine.</summary>
    public List<(short? AreaType, bool AutomaticOnly)> EditorCellQueries { get; } = [];

    public Task<IReadOnlyList<EditorCellRow>> GetCellsForEditorAsync(short? areaType, bool automaticOnly, CancellationToken ct = default)
    {
        EditorCellQueries.Add((areaType, automaticOnly));
        return Task.FromResult(Answer(nameof(GetCellsForEditorAsync), () => (IReadOnlyList<EditorCellRow>)EditorCells));
    }

    /// <summary>
    /// Le righe del controllo di configurazione (compito 7). Vuoto - il caso predefinito - vuol
    /// dire configurazione a posto, che e' un dato: il guasto si chiede con
    /// <see cref="Unavailable"/> o con <see cref="UnavailableOperations"/>.
    /// </summary>
    public List<ConfigurationWarning> ConfigurationWarnings { get; } = [];

    /// <summary>Le modalita' con cui il controllo e' stato chiesto, nell'ordine.</summary>
    public List<int> ConfigurationChecks { get; } = [];

    public Task<IReadOnlyList<ConfigurationWarning>> GetConfigurationWarningsAsync(int mode, CancellationToken ct = default)
    {
        ConfigurationChecks.Add(mode);
        return Task.FromResult(Answer(nameof(GetConfigurationWarningsAsync), () => (IReadOnlyList<ConfigurationWarning>)ConfigurationWarnings));
    }
}
