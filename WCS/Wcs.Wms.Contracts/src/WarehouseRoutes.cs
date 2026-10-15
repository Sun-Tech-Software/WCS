namespace Wcs.Wms.Contracts;

/// <summary>I percorsi del confine, condivisi fra il client del WCS e il controller del gestionale.</summary>
public static class WarehouseRoutes
{
    public const string Prefix = "/api/wcs/warehouse";
    public const string ApiKeyHeader = "X-Api-Key";

    public const string Cell = "cells/{cellId}";
    public const string Reservations = "reservations";
    public const string Reservation = "reservations/{missionId}";
    public const string ReleaseReservation = "reservations/release";

    /// <summary>
    /// Item 54: the WCS gives back a cell it had reserved for a load unit, by load unit and cell
    /// (<see cref="ReleaseLoadUnitCellRequest"/>). Always 200 with the outcome in the body: «no longer
    /// held» is an answer, and the WCS takes its queued row out for either.
    /// </summary>
    public const string ReleaseLoadUnitCell = "reservations/release-load-unit-cell";

    /// <summary>
    /// Item 54: the cells suited to a load unit, for the operator to choose one. <b>POST although it is
    /// a reading</b>: the search lists travel in the body (<see cref="StorageCellCandidatesRequest"/>).
    /// Always 200: an empty list is an answer.
    /// </summary>
    public const string StorageCellCandidates = "reservations/candidates";

    /// <summary>
    /// Item 54: reserve a precise cell, named by its door A code (<see cref="ReserveStorageCellRequest"/>).
    /// Always 200 with the outcome in the body (<see cref="StorageCellReservationResult"/>): a refused
    /// cell is an answer.
    /// </summary>
    public const string ReserveStorageCell = "reservations/cell";
    public const string LoadUnitLocation = "load-units/{loadUnitId}/location";
    public const string PendingNoteWork = "notes/pending-work";
    public const string RejectNoteAssignment = "notes/reject-assignment";

    /// <summary>
    /// Fra le assegnazioni di nota date, quelle la cui lavorazione non e' piu' in corso (compito
    /// 6). Stessa forma della gemella <see cref="PausedExtractionRows"/> - <b>POST benche' sia una
    /// lettura</b>, perche' l'elenco degli identificativi viaggia nel corpo
    /// (<see cref="PausedAmongRequest"/>) - e rotta diversa perche' la chiave e' un'altra: qui le
    /// assegnazioni (<c>ASSEGNAZIONE_NOTE</c>), di la' le righe di estrazione. Sempre 200, anche
    /// con l'elenco vuoto: la risposta e' una lista, eventualmente vuota, e non c'e' nessuna
    /// assenza da segnalare con un 404.
    /// </summary>
    public const string PausedNoteAssignments = "notes/paused-among";

    /// <summary>
    /// La segnalazione, non l'ordine: l'unita' di carico ha lasciato la coda di estrazione del WCS.
    /// Prende il posto di <c>load-units/clear-reserved-position</c> e <c>notes/mark-unassigned</c>,
    /// che dicevano al gestionale che cosa scrivere.
    /// </summary>
    public const string LoadUnitLeftExtractionQueue = "load-units/left-extraction-queue";

    public const string PendingExtractionWork = "extractions/pending-work";

    /// <summary>
    /// Fra le righe di estrazione date, quelle la cui lavorazione non e' piu' in corso (compito 5).
    /// <b>POST benche' sia una lettura</b>: l'elenco degli identificativi puo' essere lungo
    /// centinaia di voci e viaggia nel corpo (<see cref="PausedAmongRequest"/>), non nella query
    /// string. Sempre 200, anche con l'elenco vuoto: la risposta e' una lista, eventualmente vuota,
    /// e non c'e' nessuna assenza da segnalare con un 404.
    /// </summary>
    public const string PausedExtractionRows = "extractions/paused-among";

    public const string ExtractionOutcomes = "extractions/outcomes";

    /// <summary>
    /// Il fatto che ha preso il posto di <c>assignments/release</c> (compito 21): la missione e'
    /// uscita dal motore senza servire l'assegnazione che portava.
    /// </summary>
    public const string MissionLeftAssignmentUnserved = "assignments/mission-left-unserved";

    /// <summary>Il fatto opposto: l'unita' di carico e' arrivata e l'assegnazione e' stata servita.</summary>
    public const string MissionServedAssignment = "assignments/mission-served";

    /// <summary>La riga di coda che non si puo' servire, senza nessuna missione di mezzo.</summary>
    public const string UnservableQueuedExtraction = "assignments/queued-extraction-unservable";

    public const string ConfirmServedAssignments = "assignments/confirm-served";

    /// <summary>
    /// Il fatto della voce 45: l'estrazione che la missione aveva gia' prelevato e' diventata un
    /// deposito, o quella ancora nella sua cella e' diventata un cambio cella (con la sola riga di
    /// estrazione). Sempre 200 con l'esito nel corpo (<see cref="PickedExtractionBecameDepositResult"/>):
    /// «ordine gia' chiuso» e «niente da riaprire» sono risposte; 409 per lo stesso
    /// <c>OperationId</c> con un contenuto diverso.
    /// </summary>
    public const string PickedExtractionBecameDeposit = "assignments/picked-extraction-became-deposit";

    public const string AllocateLoadUnit = "load-units/allocate";
    public const string ReleaseLoadUnit = "load-units/release";
    public const string AreaTopology = "warehouses/{warehouseId}/topology";
    public const string FindLoadUnit = "load-units/find";
    public const string FindLoadUnitByReference = "load-units/find-by-reference";
    public const string PickUpDoor = "load-units/pickup-door";
    public const string CellPartitionMap = "cells/{cellId}/partitions";
    public const string CellsByDoor = "doors/{doorCode}/cells";
    public const string CellLock = "cells/lock";
    public const string CellContent = "cells/{cellId}/content";

    /// <summary>
    /// Le aree di prelievo servite da una cella polmone, in ordine di priorita' (compito 2). Una
    /// cella che non ne serve nessuna risponde 200 con <c>[]</c>: l'assenza e' un dato, non un 404.
    /// </summary>
    public const string PickingBufferAreas = "cells/{cellId}/picking-buffer-areas";

    public const string CellPositions = "cells/{cellId}/positions";
    public const string ClearCellPositions = "cells/clear-positions";
    public const string CellBusyState = "cells/busy-state";
    public const string CellStoringConstraints = "cells/storing-constraints";
    public const string LoadUnit = "load-units/{loadUnitId}/description";
    public const string LoadUnitStored = "load-units/by-code/{code}/stored";
    public const string LoadUnitByCode = "load-units/by-code/{code}";

    /// <summary>
    /// La modalita' di movimentazione per <b>tipo</b> di unita' di carico: chiave diversa da
    /// <see cref="LoadUnit"/>, quindi rotta a parte.
    /// </summary>
    public const string LoadUnitTypeHandlingMode = "load-unit-types/{loadUnitTypeId}/handling-mode";

    /// <summary>
    /// L'unita' di carico e' assegnata a una nota (compito 3). Sempre 200: un'unita' di carico che
    /// non compare in <c>ASSEGNAZIONE_NOTE</c> - compresa quella che non esiste - risponde
    /// <c>assignedToNote: false</c>, che e' la risposta, non un'assenza da segnalare con un 404.
    /// </summary>
    public const string LoadUnitAssignmentState = "load-units/{loadUnitId}/assignment-state";

    /// <summary>
    /// L'area va deframmentata per un tipo di unita' di carico (compito 4). Il tipo sta nella query
    /// string (<c>?loadUnitTypeId=</c>) e non nel percorso: il percorso nomina la cosa di cui si
    /// parla - l'area - e il tipo e' il filtro con cui la si guarda, come il
    /// <c>?limitOneShippingNotePerBuffer=</c> di <see cref="PendingNoteWork"/>. Sempre 200: un'area
    /// senza celle di quel tipo risponde <c>needed: false</c>, che e' la risposta e non un'assenza
    /// da segnalare con un 404.
    /// </summary>
    public const string AreaDefragmentation = "areas/{areaId}/defragmentation";

    /// <summary>
    /// Le celle dell'elenco dell'Editor (compito 7), con i due filtri nella query string:
    /// <c>?areaType=</c> (assente vuol dire le aree di stoccaggio, tipo 0 e 1) e
    /// <c>?automaticOnly=</c>. Percorso senza segmenti proprio perche' la domanda non ha una
    /// chiave: e' l'elenco, e i due filtri sono il modo di guardarlo - la stessa distinzione fra
    /// percorso e query string di <see cref="AreaDefragmentation"/>. Non collide con
    /// <see cref="Cell"/> (<c>cells/{cellId}</c>), che e' un'altra rotta e un'altra lettura.
    /// Sempre 200: un elenco vuoto e' un dato, non un'assenza da segnalare con un 404.
    /// </summary>
    public const string EditorCells = "cells";

    /// <summary>
    /// Il controllo di configurazione dell'impianto (compito 7), con la modalita' nella query
    /// string (<c>?mode=</c>). Sempre 200: nessuna riga vuol dire configurazione a posto.
    /// </summary>
    public const string ConfigurationCheck = "configuration-check";
}
