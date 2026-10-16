using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Wcs.Wms.Contracts;

namespace Wcs.Wms.Client;

/// <summary>
/// L'unica implementazione vera di <see cref="IWarehouse"/>: parla HTTP con il gestionale.
/// Qui sta la regola del confine su che cosa è una risposta e che cosa è un guasto:
/// <list type="bullet">
/// <item>un errore di trasporto (tempo scaduto, connessione rifiutata) o uno stato 5xx/4xx che
/// non è previsto dal metodo diventano <see cref="WarehouseUnavailableException"/>. Chi la
/// riceve non decide nulla: lascia la missione in attesa e riprova;</item>
/// <item>un <c>404</c> su una lettura che ammette «non c'è» è una risposta di dominio, non un
/// guasto: diventa <see langword="null"/>. Sono quelle che passano da <see cref="GetOrNullAsync{T}"/>
/// o <see cref="PostOrNullAsync{TRequest,TResponse}"/>, e si riconoscono dal tipo di ritorno
/// annullabile del contratto — <see cref="LocateLoadUnitAsync"/>, <see cref="FindLoadUnitAsync"/>,
/// <see cref="DescribeLoadUnitAsync"/>, <see cref="ResolveLoadUnitByCodeAsync"/>,
/// <see cref="GetStorageReservationAsync"/> e
/// <see cref="GetLoadUnitTypeHandlingModeAsync"/>.</item>
/// <item>qualunque cosa impedisca di ottenere una risposta utile — trasporto rotto, stato
/// inatteso, ma anche un <c>200</c> con un corpo che non si legge (JSON malformato, tipo di
/// contenuto inatteso) — è comunque un guasto e diventa <see cref="WarehouseUnavailableException"/>.
/// L'unica eccezione a questa regola è l'annullamento chiesto dal chiamante tramite il suo
/// <see cref="CancellationToken"/>: quello non è un'indisponibilità del gestionale e risale
/// come tale, non avvolto.</item>
/// </list>
/// </summary>
public sealed class HttpWarehouse(HttpClient http, HttpWarehouseOptions options) : IWarehouse
{
    public Task<CellDescription> DescribeCellAsync(decimal cellId, CancellationToken cancellationToken = default) =>
        GetAsync<CellDescription>("DescribeCell", FillId(WarehouseRoutes.Cell, cellId), cancellationToken);

    public Task<StorageReservation> ReserveStorageAsync(ReserveStorageRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<ReserveStorageRequest, StorageReservation>("ReserveStorage", WarehouseRoutes.Reservations, request, cancellationToken);

    public Task<StorageReservation?> GetStorageReservationAsync(Guid missionId, CancellationToken cancellationToken = default) =>
        GetOrNullAsync<StorageReservation>("GetStorageReservation",
            FillSegment(WarehouseRoutes.Reservation, missionId.ToString()), cancellationToken);

    public Task ReleaseStorageReservationAsync(Guid missionId, CancellationToken cancellationToken = default) =>
        PostAsync("ReleaseStorageReservation", WarehouseRoutes.ReleaseReservation,
            new ReleaseReservationRequest(missionId), cancellationToken);

    /// <remarks>
    /// Item 54: a POST that waits for its answer, the outcome as a number in the body of the 200. Every
    /// other status - a 4xx included - and a body that does not read become
    /// <see cref="WarehouseUnavailableException"/>; the engine's queue tells the rejection from the
    /// failure with <see cref="WarehouseUnavailableException.IsPermanentRejection"/>.
    /// </remarks>
    public Task<LoadUnitCellReleaseOutcome> ReleaseLoadUnitCellAsync(ReleaseLoadUnitCellRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<ReleaseLoadUnitCellRequest, LoadUnitCellReleaseOutcome>("ReleaseLoadUnitCell",
            WarehouseRoutes.ReleaseLoadUnitCell, request, cancellationToken);

    /// <remarks>
    /// Item 54: <b>a POST although it is a reading</b>, because the search lists travel in the body, and
    /// strict: the answer is a list, and an empty one is «no cell suits it». A 404 is a failure like any
    /// other unexpected status.
    /// </remarks>
    public Task<IReadOnlyList<StorageCellCandidate>> ListStorageCellCandidatesAsync(StorageCellCandidatesRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<StorageCellCandidatesRequest, IReadOnlyList<StorageCellCandidate>>("ListStorageCellCandidates",
            WarehouseRoutes.StorageCellCandidates, request, cancellationToken);

    /// <remarks>
    /// Item 54: the refusals (unknown door, door B, cell not available, unknown load unit) are in the
    /// body of the 200; a body without the outcome does not read, and is a failure.
    /// </remarks>
    public Task<StorageCellReservationResult> ReserveStorageCellAsync(ReserveStorageCellRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<ReserveStorageCellRequest, StorageCellReservationResult>("ReserveStorageCell",
            WarehouseRoutes.ReserveStorageCell, request, cancellationToken);

    /// <remarks>
    /// Item 55: a 404, or a 501 (the guide tells an adapter to answer 5xx for a route it has not
    /// implemented yet), is a warehouse without the check, <see langword="null"/>. Any other failure,
    /// a 503 among them, is a failure.
    /// </remarks>
    public async Task<StorageCellReservationResult?> CheckStorageCellAsync(ReserveStorageCellRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            return await PostOrNullAsync<ReserveStorageCellRequest, StorageCellReservationResult>("CheckStorageCell",
                WarehouseRoutes.CheckStorageCell, request, cancellationToken);
        }
        catch (WarehouseUnavailableException ex) when (ex.StatusCode == HttpStatusCode.NotImplemented)
        {
            return null;
        }
    }

    public Task<LoadUnitLocation?> LocateLoadUnitAsync(decimal loadUnitId, CancellationToken cancellationToken = default) =>
        GetOrNullAsync<LoadUnitLocation>("LocateLoadUnit", FillId(WarehouseRoutes.LoadUnitLocation, loadUnitId), cancellationToken);

    public Task<IReadOnlyList<PendingNote>> GetPendingNoteWorkAsync(bool limitOneShippingNotePerBuffer, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<PendingNote>>("GetPendingNoteWork",
            $"{WarehouseRoutes.PendingNoteWork}?limitOneShippingNotePerBuffer={(limitOneShippingNotePerBuffer ? "true" : "false")}", cancellationToken);

    public Task RejectNoteAssignmentAsync(RejectNoteAssignmentRequest request, CancellationToken cancellationToken = default) =>
        PostAsync("RejectNoteAssignment", WarehouseRoutes.RejectNoteAssignment, request, cancellationToken);

    /// <remarks>
    /// La gemella di <see cref="GetPausedExtractionRowsAsync"/> per le assegnazioni di nota, e
    /// valgono le stesse cose: <b>POST benche' sia una lettura</b> perche' gli identificativi
    /// viaggiano nel corpo, lettura <b>severa</b> perche' «nessuna in pausa» e' la lista vuota con
    /// un 200 e quindi un 404 e' un guasto, e l'elenco vuoto risponde <b>qui</b> senza un giro di
    /// rete. A distinguere le due domande e' la rotta: la chiave non e' la stessa.
    /// </remarks>
    /// <remarks>Item D119: a 404 or a 501 is a warehouse without the route, <see langword="null"/>.</remarks>
    public async Task<IReadOnlyList<decimal>?> GetMissingNoteAssignmentsAsync(IReadOnlyList<decimal> assignmentIds, CancellationToken cancellationToken = default)
    {
        if (assignmentIds.Count == 0) return [];
        try
        {
            return await PostOrNullAsync<PausedAmongRequest, IReadOnlyList<decimal>>("GetMissingNoteAssignments",
                WarehouseRoutes.MissingNoteAssignments, new PausedAmongRequest(assignmentIds), cancellationToken);
        }
        catch (WarehouseUnavailableException ex) when (ex.StatusCode == HttpStatusCode.NotImplemented)
        {
            return null;
        }
    }

    public Task<IReadOnlyList<decimal>> GetPausedNoteAssignmentsAsync(IReadOnlyList<decimal> assignmentIds, CancellationToken cancellationToken = default) =>
        assignmentIds.Count == 0
            ? Task.FromResult<IReadOnlyList<decimal>>([])
            : PostAsync<PausedAmongRequest, IReadOnlyList<decimal>>("GetPausedNoteAssignments",
                WarehouseRoutes.PausedNoteAssignments, new PausedAmongRequest(assignmentIds), cancellationToken);

    public Task ReportLoadUnitLeftExtractionQueueAsync(LoadUnitLeftExtractionQueueRequest request, CancellationToken cancellationToken = default) =>
        PostAsync("ReportLoadUnitLeftExtractionQueue", WarehouseRoutes.LoadUnitLeftExtractionQueue, request, cancellationToken);

    public Task<IReadOnlyList<PendingExtraction>> GetPendingExtractionWorkAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<PendingExtraction>>("GetPendingExtractionWork", WarehouseRoutes.PendingExtractionWork, cancellationToken);

    /// <remarks>
    /// <b>POST benche' sia una lettura</b>, e severa: gli identificativi sono tanti e viaggiano nel
    /// corpo (il perche' sta su <see cref="PausedAmongRequest"/>), e la risposta e' una lista -
    /// vuota quando nessuna delle righe date e' in pausa, che e' un dato. Un 404 e' quindi un
    /// guasto come ogni altro stato inatteso, e <see cref="PostAsync{TRequest,TResponse}"/> lo
    /// traduce in <see cref="WarehouseUnavailableException"/>.
    /// <para>
    /// Con l'elenco vuoto la risposta e' la lista vuota e si da' <b>qui</b>, senza un giro di rete:
    /// stessa ragione di <see cref="IsLoadUnitStoredAsync"/> - non ha bisogno del gestionale per
    /// essere data - e il gestionale la da' a sua volta senza toccare il database, perche' il
    /// contratto non promette che la scorciatoia stia da questo lato.
    /// </para>
    /// </remarks>
    public Task<IReadOnlyList<decimal>> GetPausedExtractionRowsAsync(IReadOnlyList<decimal> extractionRowIds, CancellationToken cancellationToken = default) =>
        extractionRowIds.Count == 0
            ? Task.FromResult<IReadOnlyList<decimal>>([])
            : PostAsync<PausedAmongRequest, IReadOnlyList<decimal>>("GetPausedExtractionRows",
                WarehouseRoutes.PausedExtractionRows, new PausedAmongRequest(extractionRowIds), cancellationToken);

    public Task ReportExtractionOutcomesAsync(ReportExtractionOutcomesRequest request, CancellationToken cancellationToken = default) =>
        PostAsync("ReportExtractionOutcomes", WarehouseRoutes.ExtractionOutcomes, request, cancellationToken);

    public Task ReportMissionLeftAssignmentUnservedAsync(MissionLeftAssignmentUnservedRequest request, CancellationToken cancellationToken = default) =>
        PostAsync("ReportMissionLeftAssignmentUnserved", WarehouseRoutes.MissionLeftAssignmentUnserved, request, cancellationToken);

    public Task ReportMissionServedAssignmentAsync(MissionServedAssignmentRequest request, CancellationToken cancellationToken = default) =>
        PostAsync("ReportMissionServedAssignment", WarehouseRoutes.MissionServedAssignment, request, cancellationToken);

    public Task ReportUnservableQueuedExtractionAsync(UnservableQueuedExtractionRequest request, CancellationToken cancellationToken = default) =>
        PostAsync("ReportUnservableQueuedExtraction", WarehouseRoutes.UnservableQueuedExtraction, request, cancellationToken);

    public Task ConfirmServedAssignmentsAsync(ConfirmServedAssignmentsRequest request, CancellationToken cancellationToken = default) =>
        PostAsync("ConfirmServedAssignments", WarehouseRoutes.ConfirmServedAssignments, request, cancellationToken);

    /// <remarks>
    /// Una segnalazione che <b>aspetta una risposta</b>: l'esito sta nel corpo del 200, quindi passa
    /// dalla <see cref="PostAsync{TRequest,TResponse}"/> severa. Ogni stato diverso da un 2xx - il
    /// 409 del conflitto compreso - e ogni corpo che non si legge, compreso quello senza i due esiti
    /// obbligatori, diventano <see cref="WarehouseUnavailableException"/>: e' la coda del chiamante a
    /// distinguere il rifiuto dal guasto con <see cref="WarehouseUnavailableException.IsPermanentRejection"/>.
    /// </remarks>
    public Task<PickedExtractionBecameDepositResult> ReportPickedExtractionBecameDepositAsync(PickedExtractionBecameDepositRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<PickedExtractionBecameDepositRequest, PickedExtractionBecameDepositResult>("ReportPickedExtractionBecameDeposit",
            WarehouseRoutes.PickedExtractionBecameDeposit, request, cancellationToken);

    public Task AllocateLoadUnitAsync(AllocateLoadUnitRequest request, CancellationToken cancellationToken = default) =>
        PostAsync("AllocateLoadUnit", WarehouseRoutes.AllocateLoadUnit, request, cancellationToken);

    public Task ReleaseLoadUnitAsync(ReleaseLoadUnitRequest request, CancellationToken cancellationToken = default) =>
        PostAsync("ReleaseLoadUnit", WarehouseRoutes.ReleaseLoadUnit, request, cancellationToken);

    public Task<AreaTopology> GetAreaTopologyAsync(decimal warehouseId, CancellationToken cancellationToken = default) =>
        GetAsync<AreaTopology>("GetAreaTopology", FillId(WarehouseRoutes.AreaTopology, warehouseId), cancellationToken);

    public Task<LoadUnitSearchResult?> FindLoadUnitAsync(FindLoadUnitRequest request, CancellationToken cancellationToken = default) =>
        PostOrNullAsync<FindLoadUnitRequest, LoadUnitSearchResult>("FindLoadUnit", WarehouseRoutes.FindLoadUnit, request, cancellationToken);

    public Task<ReferenceSearchResult> FindLoadUnitByReferenceAsync(FindLoadUnitByReferenceRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<FindLoadUnitByReferenceRequest, ReferenceSearchResult>("FindLoadUnitByReference",
            WarehouseRoutes.FindLoadUnitByReference, request, cancellationToken);

    public Task<PickUpDoorChoice> ChoosePickUpDoorAsync(decimal loadUnitId, decimal cellId, CancellationToken cancellationToken = default) =>
        PostAsync<ChoosePickUpDoorRequest, PickUpDoorChoice>("ChoosePickUpDoor", WarehouseRoutes.PickUpDoor,
            new ChoosePickUpDoorRequest(loadUnitId, cellId), cancellationToken);

    public Task<CellPartitionMap> GetCellPartitionMapAsync(decimal cellId, CancellationToken cancellationToken = default) =>
        GetAsync<CellPartitionMap>("GetCellPartitionMap", FillId(WarehouseRoutes.CellPartitionMap, cellId), cancellationToken);

    public Task<IReadOnlyList<CellDescription>> DescribeCellsByDoorAsync(string doorCode, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<CellDescription>>("DescribeCellsByDoor", FillSegment(WarehouseRoutes.CellsByDoor, doorCode), cancellationToken);

    public Task SetCellLockStateAsync(string doorCode, CellLockState lockState, CancellationToken cancellationToken = default) =>
        PostAsync("SetCellLockState", WarehouseRoutes.CellLock, new SetCellLockStateRequest(doorCode, lockState), cancellationToken);

    public Task<IReadOnlyList<decimal>> GetCellContentAsync(decimal cellId, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<decimal>>("GetCellContent", FillId(WarehouseRoutes.CellContent, cellId), cancellationToken);

    /// <summary>
    /// Le aree servite dal polmone, in ordine di priorità (compito 2). Lettura <b>severa</b>: la
    /// cella che non serve nessuna area risponde <c>[]</c>, quindi non c'è nessun «non c'è» da
    /// tradurre e un 404 è un guasto come ogni altro stato inatteso.
    /// </summary>
    public Task<IReadOnlyList<decimal>> GetPickingBufferAreasAsync(decimal cellId, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<decimal>>("GetPickingBufferAreas",
            FillId(WarehouseRoutes.PickingBufferAreas, cellId), cancellationToken);

    public Task<IReadOnlyList<CellPosition>> GetCellPositionsAsync(decimal cellId, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<CellPosition>>("GetCellPositions", FillId(WarehouseRoutes.CellPositions, cellId), cancellationToken);

    public Task ClearCellPositionsAsync(decimal cellId, CancellationToken cancellationToken = default) =>
        PostAsync("ClearCellPositions", WarehouseRoutes.ClearCellPositions,
            new ClearCellPositionsRequest(cellId), cancellationToken);

    public Task<CellBusyState> GetCellBusyStateAsync(decimal cellId, decimal loadUnitIdToExclude = 0, CancellationToken cancellationToken = default) =>
        PostAsync<CellBusyStateRequest, CellBusyState>("GetCellBusyState", WarehouseRoutes.CellBusyState,
            new CellBusyStateRequest(cellId, loadUnitIdToExclude), cancellationToken);

    public Task<CellStoringConstraints> GetCellStoringConstraintsAsync(decimal cellId, CellDoor door, int loadUnitDepth, decimal loadUnitIdToExclude, CancellationToken cancellationToken = default) =>
        PostAsync<CellStoringConstraintsRequest, CellStoringConstraints>("GetCellStoringConstraints", WarehouseRoutes.CellStoringConstraints,
            new CellStoringConstraintsRequest(cellId, door, loadUnitDepth, loadUnitIdToExclude), cancellationToken);

    public Task<LoadUnitDescription?> DescribeLoadUnitAsync(decimal loadUnitId, CancellationToken cancellationToken = default) =>
        GetOrNullAsync<LoadUnitDescription>("DescribeLoadUnit", FillId(WarehouseRoutes.LoadUnit, loadUnitId), cancellationToken);

    /// <summary>
    /// La modalita' di movimentazione del tipo di UDC. E' una lettura <b>lasca</b>: un tipo che non
    /// esiste risponde 404 e qui diventa <see langword="null"/>, mentre ogni altro guasto diventa
    /// <see cref="WarehouseUnavailableException"/>.
    /// </summary>
    public Task<LoadUnitTypeHandlingMode?> GetLoadUnitTypeHandlingModeAsync(decimal loadUnitTypeId, CancellationToken cancellationToken = default) =>
        GetOrNullAsync<LoadUnitTypeHandlingMode>("GetLoadUnitTypeHandlingMode",
            FillId(WarehouseRoutes.LoadUnitTypeHandlingMode, loadUnitTypeId), cancellationToken);

    /// <summary>
    /// L'unita' di carico e' assegnata a una nota (compito 3). Lettura <b>severa</b>: la domanda ha
    /// sempre una risposta - «non assegnata» arriva come un 200 con <c>assignedToNote: false</c>,
    /// anche per un'unita' di carico che non esiste - quindi non c'e' nessun «non c'e'» da tradurre
    /// e un 404 e' un guasto come ogni altro stato inatteso.
    /// </summary>
    public Task<LoadUnitAssignmentState> GetLoadUnitAssignmentStateAsync(decimal loadUnitId, CancellationToken cancellationToken = default) =>
        GetAsync<LoadUnitAssignmentState>("GetLoadUnitAssignmentState",
            FillId(WarehouseRoutes.LoadUnitAssignmentState, loadUnitId), cancellationToken);

    /// <summary>
    /// L'area va deframmentata per questo tipo di unita' di carico (compito 4). Lettura
    /// <b>severa</b>: la domanda ha sempre una risposta - un'area senza celle di quel tipo non va
    /// deframmentata - quindi non c'e' nessun «non c'e'» da tradurre e un 404 e' un guasto come
    /// ogni altro stato inatteso.
    /// <para>
    /// L'area sta nel percorso e il tipo nella query string, che si scrive con
    /// <see cref="FormattableString.Invariant"/>: <see cref="FillId"/> passa gia' dalla cultura
    /// invariante per il segmento, e senza <c>Invariant</c> qui un <see cref="decimal"/> con parte
    /// frazionaria partirebbe con la virgola su una macchina italiana.
    /// </para>
    /// </summary>
    public Task<AreaDefragmentationVerdict> GetAreaDefragmentationVerdictAsync(decimal areaId, decimal loadUnitTypeId, CancellationToken cancellationToken = default) =>
        GetAsync<AreaDefragmentationVerdict>("GetAreaDefragmentationVerdict",
            FormattableString.Invariant($"{FillId(WarehouseRoutes.AreaDefragmentation, areaId)}?loadUnitTypeId={loadUnitTypeId}"),
            cancellationToken);

    /// <summary>
    /// Le celle dell'elenco dell'Editor (compito 7). Lettura <b>severa</b>: un impianto senza celle
    /// di quel tipo risponde <c>[]</c> con un 200, quindi non c'e' nessun «non c'e'» da tradurre e
    /// un 404 e' un guasto come ogni altro stato inatteso - l'Editor deve dire che non si e'
    /// potuto leggere, non aprire una finestra vuota che sembra un impianto da configurare.
    /// <para>
    /// I due filtri stanno nella query string e il percorso non ha segmenti: la domanda non ha una
    /// chiave, e' l'elenco. <b>Senza tipo d'area il filtro non parte affatto</b>, invece di
    /// partire vuoto: «nessun tipo» vuol dire le aree di stoccaggio (0 e 1), che e' una risposta
    /// diversa da «tipo zero», e un <c>areaType=</c> vuoto la lascerebbe decidere al legame di
    /// modello dall'altra parte. La query si scrive con <see cref="FormattableString.Invariant"/>
    /// per la stessa ragione di <see cref="GetAreaDefragmentationVerdictAsync"/>: i numeri sul filo
    /// non hanno cultura.
    /// </para>
    /// </summary>
    public Task<IReadOnlyList<EditorCellRow>> GetCellsForEditorAsync(short? areaType, bool automaticOnly, CancellationToken cancellationToken = default)
    {
        var filters = areaType is null
            ? FormattableString.Invariant($"?automaticOnly={(automaticOnly ? "true" : "false")}")
            : FormattableString.Invariant($"?areaType={areaType.Value}&automaticOnly={(automaticOnly ? "true" : "false")}");

        return GetAsync<IReadOnlyList<EditorCellRow>>("GetCellsForEditor",
            WarehouseRoutes.EditorCells + filters, cancellationToken);
    }

    /// <summary>
    /// Il controllo di configurazione dell'impianto (compito 7). Lettura <b>severa</b> per una
    /// ragione che si vede a occhio: <b>nessuna riga vuol dire configurazione a posto</b>, quindi
    /// un 404 letto come lista vuota direbbe all'Editor che l'impianto e' a posto proprio quando
    /// il gestionale non ha risposto.
    /// </summary>
    public Task<IReadOnlyList<ConfigurationWarning>> GetConfigurationWarningsAsync(int mode, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<ConfigurationWarning>>("GetConfigurationWarnings",
            FormattableString.Invariant($"{WarehouseRoutes.ConfigurationCheck}?mode={mode}"), cancellationToken);

    /// <remarks>
    /// <b>Il codice vuoto non parte.</b> Il codice e' un segmento del percorso, e un segmento vuoto
    /// non lega un parametro obbligatorio: <c>load-units/by-code//stored</c> non corrisponde a
    /// nessuna rotta e riceve un 404 di instradamento, che questa e' una lettura severa e traduce
    /// in <see cref="WarehouseUnavailableException"/>. La guardia del gestionale — che risponde
    /// falso su un codice vuoto — non veniva mai raggiunta, e la promessa del contratto era falsa
    /// nel merito. La risposta e' quella promessa, e si da' qui perche' non ha bisogno del
    /// gestionale per essere data.
    /// </remarks>
    public Task<bool> IsLoadUnitStoredAsync(string? code, CancellationToken cancellationToken = default) =>
        string.IsNullOrEmpty(code)
            ? Task.FromResult(false)
            : GetAsync<bool>("IsLoadUnitStored", FillSegment(WarehouseRoutes.LoadUnitStored, code), cancellationToken);

    /// <remarks>
    /// Stessa ragione di <see cref="IsLoadUnitStoredAsync"/>: con un codice vuoto il percorso non
    /// corrisponde a nessuna rotta. Qui la lettura e' lasca e il 404 diventava gia' <c>null</c>,
    /// che e' la risposta giusta, ma la si da' senza un giro di rete che non puo' riuscire.
    /// </remarks>
    public Task<LoadUnitIdentity?> ResolveLoadUnitByCodeAsync(string? code, CancellationToken cancellationToken = default) =>
        string.IsNullOrEmpty(code)
            ? Task.FromResult<LoadUnitIdentity?>(null)
            : GetOrNullAsync<LoadUnitIdentity>("ResolveLoadUnitByCode", FillSegment(WarehouseRoutes.LoadUnitByCode, code), cancellationToken);

    /// <summary>
    /// An answer that is an enum must be one of its values: System.Text.Json takes any number, and a
    /// value added by a newer warehouse would otherwise fall into whatever branch the caller has for
    /// «anything else» (item 55: an unknown cell release outcome was read as NotHeld).
    /// </summary>
    private static T Known<T>(string operation, T value)
    {
        if (typeof(T).IsEnum && !Enum.IsDefined(typeof(T), value!))
            throw new WarehouseUnavailableException(operation,
                $"the warehouse answered {Convert.ToInt64(value)}, which is not a {typeof(T).Name} this WCS knows");
        return value;
    }

    private async Task<T> GetAsync<T>(string operation, string path, CancellationToken cancellationToken)
    {
        try
        {
            using var request = NewRequest(HttpMethod.Get, path);
            using var response = await http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            return Known(operation, await response.Content.ReadFromJsonAsync<T>(cancellationToken)
                   ?? throw new WarehouseUnavailableException(operation));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            throw new WarehouseUnavailableException(operation, ex);
        }
    }

    /// <summary>
    /// Come <see cref="GetAsync{T}"/>, ma un 404 è la risposta «non c'è», non un guasto.
    /// Un 200 il cui corpo deserializza a <see langword="null"/> non lo è: un 404 è il gestionale
    /// che dice esplicitamente «non esiste», un 200 con corpo nullo è il gestionale che si
    /// comporta in un modo che non capiamo, e per lo stesso motivo delle altre letture è un
    /// guasto, non «non c'è».
    /// </summary>
    private async Task<T?> GetOrNullAsync<T>(string operation, string path, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            using var request = NewRequest(HttpMethod.Get, path);
            using var response = await http.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound) return null;

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
                   ?? throw new WarehouseUnavailableException(operation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            throw new WarehouseUnavailableException(operation, ex);
        }
    }

    /// <summary>
    /// Come <see cref="GetOrNullAsync{T}"/> - un 404 è la risposta «non c'è», non un guasto - ma con
    /// un corpo nella richiesta invece di un segnaposto nel percorso: serve a
    /// <see cref="FindLoadUnitAsync"/> (compito 11), la cui richiesta è troppo ricca per un GET.
    /// </summary>
    private async Task<TResponse?> PostOrNullAsync<TRequest, TResponse>(string operation, string path, TRequest body, CancellationToken cancellationToken)
        where TResponse : class
    {
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        try
        {
            using var request = NewRequest(HttpMethod.Post, path);
            request.Content = content;

            using var response = await http.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound) return null;

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken)
                   ?? throw new WarehouseUnavailableException(operation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            throw new WarehouseUnavailableException(operation, ex);
        }
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(string operation, string path, TRequest body, CancellationToken cancellationToken)
    {
        // La serializzazione del corpo sta qui, fuori dal try qui sotto: se il tipo della richiesta
        // ha un difetto che lo rende non serializzabile è un nostro errore di programmazione, e deve
        // risalire come tale invece di travestirsi da indisponibilità del gestionale. Il try cattura
        // solo i guasti della richiesta HTTP e della lettura della risposta.
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        try
        {
            using var request = NewRequest(HttpMethod.Post, path);
            request.Content = content;

            using var response = await http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            return Known(operation, await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken)
                   ?? throw new WarehouseUnavailableException(operation));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            throw new WarehouseUnavailableException(operation, ex);
        }
    }

    private async Task PostAsync<TRequest>(string operation, string path, TRequest body, CancellationToken cancellationToken)
    {
        try
        {
            using var request = NewRequest(HttpMethod.Post, path);
            request.Content = JsonContent.Create(body);

            using var response = await http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new WarehouseUnavailableException(operation, ex);
        }
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, $"{WarehouseRoutes.Prefix}/{path}");
        request.Headers.Add(WarehouseRoutes.ApiKeyHeader, options.ApiKey);
        return request;
    }

    /// <summary>Sostituisce il segnaposto fra graffe di un percorso di <see cref="WarehouseRoutes"/> con un id.</summary>
    private static string FillId(string routeTemplate, decimal id)
    {
        var openBrace = routeTemplate.IndexOf('{');
        var closeBrace = routeTemplate.IndexOf('}');
        return string.Concat(
            routeTemplate.AsSpan(0, openBrace),
            id.ToString(CultureInfo.InvariantCulture),
            routeTemplate.AsSpan(closeBrace + 1));
    }

    /// <summary>
    /// Come <see cref="FillId"/>, ma per un segmento di percorso testuale invece che numerico, quindi
    /// va con escape: un codice porta (compito 9, <see cref="WarehouseRoutes.CellsByDoor"/>), un
    /// codice unita' di carico (compito 10, <see cref="WarehouseRoutes.LoadUnitByCode"/>) o
    /// l'identificativo di una missione (compito 17, <see cref="WarehouseRoutes.Reservation"/>).
    /// Si chiamava <c>FillDoorCode</c> finche' i codici porta erano il solo caso.
    /// </summary>
    private static string FillSegment(string routeTemplate, string segment)
    {
        var openBrace = routeTemplate.IndexOf('{');
        var closeBrace = routeTemplate.IndexOf('}');
        return string.Concat(
            routeTemplate.AsSpan(0, openBrace),
            Uri.EscapeDataString(segment),
            routeTemplate.AsSpan(closeBrace + 1));
    }
}
