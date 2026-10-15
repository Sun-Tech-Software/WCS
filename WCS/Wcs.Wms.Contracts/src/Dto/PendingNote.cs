namespace Wcs.Wms.Contracts;

/// <summary>
/// Una lavorazione di nota di spedizione pronta per l'automazione (compito 14): già assegnata dal
/// gestionale, e già validata contro le tre cose che solo il WCS sa - se il motore ha già l'unità
/// di carico, se questa è già in coda per un'altra estrazione, se il polmone serve qualche area. Il
/// motore non decide più quali note assegnare né perché un'assegnazione va scartata: riceve del
/// lavoro già pronto da mettere in coda.
/// </summary>
/// <param name="AssignmentId">L'assegnazione (<c>ID_ASSEGNAZIONE_UDC</c>) che questa riga serve.</param>
/// <param name="AssignmentGuid">Il GUID dell'assegnazione, se ne ha uno: viaggia nel comando di estrazione come lo faceva prima.</param>
/// <param name="LoadUnitId">L'unità di carico assegnata.</param>
/// <param name="BufferCellId">La cella polmone di destinazione.</param>
/// <param name="BufferAreaIds">Le aree servite dal polmone (ex <c>DbInfo.PickingBufferAreas</c>): il motore le usa per restringere la ricerca dell'unità di carico, il gestionale le ha già lette per validare il terzo motivo di rifiuto.</param>
/// <param name="DestinationItemId">Il punto dell'impianto che serve il polmone, se il gestionale l'ha già risolto; vuoto se il motore deve ancora calcolarlo.</param>
/// <param name="WorkstationId">La postazione dell'assegnazione, per i comandi che il motore costruirà.</param>
/// <param name="ProcessingId">
/// <c>TESTATE_NOTE.ID_LAVORAZIONE</c>: la lavorazione della nota di spedizione che questa
/// assegnazione serve (compito 9). Il motore la rileggeva da sé, a ogni giro di ogni
/// ricollocatore, con una <c>SELECT</c> che dalla lista letterale delle assegnazioni in coda
/// risaliva a <c>RIGHE_NOTE</c> e a <c>TESTATE_NOTE</c> (<c>DbInfo.ProcessingIdsByAssignment</c>);
/// ora viaggia con l'assegnazione, si ferma nella coda del WCS e di lì arriva alla missione
/// (<c>PathMission.NoteProcessingId</c>), che è il solo posto da cui il motore la rilegge. Non è
/// annullabile e non è mai zero: sul database la colonna ammette il nullo, ma un'assegnazione
/// senza lavorazione a questo ciclo non arriva (la query fa un <c>INNER JOIN</c> su
/// <c>LAVORAZIONI_NOTE</c> proprio per quella colonna), e se ci arrivasse il gestionale
/// solleverebbe invece di rispondere zero — uno zero qui diventerebbe una nota che non si chiude
/// più, in silenzio.
/// </param>
public sealed record PendingNote(
    decimal AssignmentId,
    Guid? AssignmentGuid,
    decimal LoadUnitId,
    decimal BufferCellId,
    IReadOnlyList<decimal> BufferAreaIds,
    Guid? DestinationItemId,
    string? WorkstationId,
    decimal ProcessingId);
