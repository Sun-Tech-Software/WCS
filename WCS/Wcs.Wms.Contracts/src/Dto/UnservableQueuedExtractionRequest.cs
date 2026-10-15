namespace Wcs.Wms.Contracts;

/// <summary>
/// La segnalazione di <see cref="IWarehouse.ReportUnservableQueuedExtractionAsync"/> (compito 21):
/// una riga della coda di estrazione del WCS non si puo' servire, e l'assegnazione che stava
/// aspettando resta senza chi la porti.
/// </summary>
/// <remarks>
/// <b>Non e' <see cref="MissionLeftAssignmentUnservedRequest"/>:</b> qui una missione non c'e' mai
/// stata. Il fatto nasce mentre il motore scorre la propria coda di estrazione per costruire una
/// missione e scopre che quella riga non e' costruibile - l'unita' di carico e' gia' impegnata in
/// un'altra lavorazione, o la riga non ne ha nessuna. Chiave diversa (la riga di coda, non la
/// missione) e forma diversa, quindi operazione diversa.
/// <para>
/// La riga di coda vive nel database del WCS e il gestionale non la vede: e' per questo che il fatto
/// e' del WCS. L'unica cosa che il gestionale conosce, e che quindi viaggia, e' l'assegnazione che
/// quella riga stava servendo.
/// </para>
/// </remarks>
/// <param name="ExtractionQueueId">
/// La riga della coda di estrazione del WCS (<c>ID_ESTRAZIONE_UDC</c>). Il gestionale non ci scrive
/// niente sopra - la coda non e' sua - ma la porta nel registro, cosi' chi legge sa quale riga ha
/// innescato la cancellazione.
/// </param>
/// <param name="LoadUnitId">L'unita' di carico della riga di coda, <c>0</c> se la riga non ne ha piu' una.</param>
/// <param name="AssignmentGuid">
/// L'assegnazione intera dell'unita' di carico (<c>GUID_ASSEGNAZIONE_UDC</c>) che la riga di coda
/// stava servendo.
/// </param>
/// <param name="Reason">Perche' la riga non si puo' servire: e' quello che un operatore legge quando l'assegnazione sparisce.</param>
public sealed record UnservableQueuedExtractionRequest(
    decimal ExtractionQueueId,
    decimal LoadUnitId,
    Guid AssignmentGuid,
    string Reason);
