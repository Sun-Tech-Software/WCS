namespace Wcs.Wms.Contracts;

/// <summary>
/// Una riga di estrazione da servire (compito 15, ex <c>DbInfo.GetLuToExtractByLuExtraction</c> più
/// <c>DbInfo.GetLuLocked</c>): l'unità di carico da portare al polmone, la riga del gestionale che
/// la chiede, e se l'unità di carico è bloccata.
/// </summary>
/// <param name="ExtractionRowId">
/// <c>ID_RIGA_ESTRAZIONE_UDC</c>: la chiave con cui il motore riferisce l'esito
/// (<see cref="ExtractionRowOutcome"/>), e quella che viaggia nel comando di estrazione.
/// </param>
/// <param name="LoadUnitId">L'unità di carico da estrarre (<c>ID_UDC</c>).</param>
/// <param name="LoadUnitLocked">
/// <c>UDC.BLOCCATO</c>, letto dal gestionale insieme alla riga invece che dal motore una riga alla
/// volta: è un dato suo, e il motore non ne fa altro che riferire
/// <see cref="ExtractionRowStatus.LoadUnitLocked"/>. L'esito resta quello di prima, riga per riga.
/// </param>
/// <param name="ProcessingId">
/// <c>TESTATE_ESTRAZIONI_UDC.ID_LAVORAZIONE</c>: la lavorazione di estrazione che questa riga
/// serve (compito 8). Il motore la rileggeva da sé alla chiusura di ogni movimento
/// (<c>DbInfo.GetIdLavorazione</c>, un <c>JOIN</c> fra <c>RIGHE_ESTRAZIONI_UDC</c> e
/// <c>TESTATE_ESTRAZIONI_UDC</c> per riga chiusa); ora viaggia con la riga e si ferma nella coda
/// del WCS, che è il solo posto da cui il motore la rilegge. Non è annullabile e non è mai zero:
/// sul database la colonna ammette il nullo, ma una testata senza lavorazione non arriva a questo
/// ciclo (la query che sceglie le testate fa un <c>INNER JOIN</c> proprio su quella colonna), e se
/// ci arrivasse il gestionale solleverebbe invece di rispondere zero — uno zero qui diventerebbe
/// una lavorazione che non si chiude più, in silenzio.
/// </param>
public sealed record PendingExtractionRow(
    decimal ExtractionRowId,
    decimal LoadUnitId,
    bool LoadUnitLocked,
    decimal ProcessingId);
