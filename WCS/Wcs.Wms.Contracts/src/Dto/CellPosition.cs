namespace Wcs.Wms.Contracts;

/// <summary>
/// Una riga di <c>POSIZIONI_UDC</c> per una cella (compito 18): quale unità di carico occupa la
/// posizione, e se quella posizione è <b>prenotata</b> invece che fisica.
/// </summary>
/// <remarks>
/// Non è la stessa cosa di <see cref="IWarehouse.GetCellContentAsync"/>, che di questa cella
/// risponde le sole unità di carico nelle posizioni <b>non</b> prenotate: quella scarta apposta le
/// righe prenotate, e i suoi chiamanti contano su questo. Le due letture condividono chiave
/// (la cella) e forma (le unità di carico che ci stanno dentro, ordinate per posizione) ma non la
/// semantica, ed è per questo che sono due operazioni e non una con un parametro.
/// </remarks>
/// <param name="LoadUnitId">L'unità di carico che occupa la posizione (<c>POSIZIONI_UDC.ID_UDC</c>).</param>
/// <param name="Reserved">
/// Se la posizione è prenotata (<c>POSIZIONI_UDC.ASSEGNAZIONE = 1</c>): l'unità di carico non è
/// ancora fisicamente lì, la posizione le è stata messa da parte.
/// </param>
public sealed record CellPosition(decimal LoadUnitId, bool Reserved);
