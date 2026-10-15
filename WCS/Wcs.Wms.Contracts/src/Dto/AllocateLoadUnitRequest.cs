namespace Wcs.Wms.Contracts;

/// <summary>
/// Deposita fisicamente un'unità di carico in una cella (compito 13, ex due chiamate del motore a
/// <c>OperazioniUdc.AllocaInCella</c>, una delle quali seguita da <c>OperazioniUdc.RientroUdc</c> se
/// l'unità di carico risultava spedita). Il gestionale applica la transizione in modo atomico e usa
/// <see cref="OperationId"/> per riconoscere le ripetizioni della stessa richiesta.
/// </summary>
/// <param name="LoadUnitId">L'unità di carico da depositare.</param>
/// <param name="CellId">
/// La cella di destinazione quando <paramref name="StorageDoorCode"/> è vuoto - il caso della cella
/// operatore (<c>AutomationDevice.LinkedCellId</c>): il gestionale non la rilegge dal database, la
/// tratta come una cella dell'area Operatori con porta A. Ignorato quando
/// <paramref name="StorageDoorCode"/> è valorizzato.
/// </param>
/// <param name="StorageDoorCode">
/// Il codice porta della cella di stoccaggio, quando il deposito è in una cella vera (non una cella
/// operatore): il gestionale rilegge la cella da questo codice, non da <paramref name="CellId"/> -
/// e' la stessa lettura per codice porta che il motore faceva in processo, per non alterare la
/// stranezza preesistente sulla porta selezionata mai impostata (vedi il rapporto del compito 9).
/// Vuoto o nullo per il caso della cella operatore.
/// </param>
/// <param name="ResetAssignment">Se vero, elimina prima un'eventuale assegnazione precedente dell'unità di carico.</param>
/// <param name="Manual">Vero se il deposito è manuale, falso se automatico.</param>
/// <param name="StorageDepth">
/// La quota di deposito già decisa dal motore (compito 9, <see cref="CellStoringConstraints"/>) per
/// una cella multiprofondità; -1 per lasciarla calcolare al gestionale. Ignorata dal gestionale per
/// il caso della cella operatore, come ignorava il parametro equivalente la chiamata originale.
/// </param>
/// <param name="DetectedDepth">La posizione rilevata fisicamente, se nota; altrimenti zero.</param>
/// <param name="RequestedByUserId">L'utente/processo che sta depositando, per la tracciabilità della scrittura.</param>
/// <param name="OperationId">
/// Identità stabile della transizione, conservata durante tutti i tentativi. <see cref="Guid.Empty"/>
/// indica un chiamante legacy: il gestionale esegue la richiesta senza registrarne la deduplicazione.
/// </param>
public sealed record AllocateLoadUnitRequest(
    decimal LoadUnitId,
    decimal CellId,
    string? StorageDoorCode,
    bool ResetAssignment,
    bool Manual,
    int StorageDepth,
    int DetectedDepth,
    string RequestedByUserId,
    Guid OperationId = default);
