namespace Wcs.Wms.Contracts;

/// <summary>
/// L'esito di una riga di estrazione dopo il giro del motore (compito 15). Le righe che il motore
/// <b>non</b> ha potuto nemmeno provare a risolvere - la guida dell'impianto non si risolve, manca
/// il punto di origine o di destinazione nel diagramma - non hanno un esito: non compaiono
/// nell'elenco di <see cref="ReportExtractionOutcomesRequest.Outcomes"/>, perché non scrivere non è
/// lo stesso che scrivere zero, ed era così anche quando la scrittura la faceva il motore.
/// </summary>
/// <param name="ExtractionRowId">La riga di estrazione (<c>ID_RIGA_ESTRAZIONE_UDC</c>).</param>
/// <param name="Status">Che cosa le è successo in questo giro.</param>
public sealed record ExtractionRowOutcome(
    decimal ExtractionRowId,
    ExtractionRowStatus Status);
