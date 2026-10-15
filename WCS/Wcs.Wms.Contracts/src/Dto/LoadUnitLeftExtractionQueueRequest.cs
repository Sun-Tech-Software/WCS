namespace Wcs.Wms.Contracts;

/// <summary>
/// La segnalazione di <see cref="IWarehouse.ReportLoadUnitLeftExtractionQueueAsync"/>: un fatto del
/// WCS, non un ordine al gestionale.
/// </summary>
/// <param name="LoadUnitId">L'unita' di carico che ha lasciato la coda.</param>
/// <param name="AssignmentId">
/// L'assegnazione che la riga di coda stava servendo, <c>0</c> se non ne serviva nessuna. E' un
/// dato del WCS - sta sulla riga della coda, nel database del WCS - e senza di esso il gestionale
/// non saprebbe quale assegnazione il fatto riguarda. Che cosa comportarne lo decide lui.
/// </param>
public sealed record LoadUnitLeftExtractionQueueRequest(decimal LoadUnitId, decimal AssignmentId);
