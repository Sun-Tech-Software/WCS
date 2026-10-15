namespace Wcs.Wms.Contracts;

/// <summary>
/// L'esito di una richiesta di prenotazione di deposito (compito 12, ex
/// <c>WMS.Services.Interface.Models.RisultatoRicercaDestinazioneUdc</c>). Appartiene alla missione:
/// finché la missione vive e la prenotazione non è revocata, quella cella è sua. La versione
/// precedente portava solo la cella, l'area e una profondità che nessun chiamante impostava mai
/// (<see cref="ReserveStorageRequest"/>) - perdeva cioè quasi tutto quello che
/// <c>PathMission.SetDataForStoreInAnyAvailableCellDifferentStoreLocation</c>,
/// <c>SetDataForStoreInLinkedCell</c> e <c>SetDataForCheckExistsAvailableCell</c> leggono davvero dal
/// risultato originale: l'esito stesso (dodici valori, non un booleano trovato/non trovato), il
/// codice porta, la partizione di deposito e la quota. Con questi cinque campi popolati da un'unica
/// risposta, il motore non ha più bisogno di interrogare separatamente
/// <see cref="IWarehouse.GetCellPartitionMapAsync"/> o <see cref="IWarehouse.GetCellStoringConstraintsAsync"/>
/// dopo una prenotazione riuscita.
/// </summary>
/// <param name="MissionId">La missione a cui appartiene, lo stesso valore della richiesta.</param>
/// <param name="Outcome">L'esito, a dodici valori: solo <see cref="StorageReservationOutcome.CellFound"/> scrive davvero una posizione.</param>
/// <param name="CellId">La cella scelta; zero se <paramref name="Outcome"/> non è <see cref="StorageReservationOutcome.CellFound"/>.</param>
/// <param name="DoorCode">Il codice della porta della cella scelta.</param>
/// <param name="Partition">La partizione di deposito già risolta dal gestionale (mai "da calcolare": è "0000" per le celle non partizionate).</param>
/// <param name="StorageDepth">La quota di deposito assegnata (o teorica, se la richiesta non chiedeva di scrivere).</param>
/// <param name="NeedsCompaction">Se la cella, dopo questo deposito, ha bisogno di essere compattata.</param>
public sealed record StorageReservation(
    Guid MissionId,
    StorageReservationOutcome Outcome,
    decimal CellId,
    string DoorCode,
    string? Partition,
    int StorageDepth,
    bool NeedsCompaction);
