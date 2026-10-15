namespace Wcs.Wms.Contracts;

/// <summary>
/// L'unità di carico che porta la referenza cercata, e la cella in cui sta (compito 20).
/// <para>
/// <b>Due campi su una trentina.</b> Il gestionale risponde <c>CellaReferenza</c>: magazzino,
/// descrizioni, colonna, piano, distinta, quantità, quantità assegnata, ostacoli. Di tutto questo
/// il motore leggeva l'unità di carico e la cella, e le passava subito a
/// <see cref="IWarehouse.ChoosePickUpDoorAsync"/> e <see cref="IWarehouse.DescribeLoadUnitAsync"/>:
/// il resto non aveva un lettore, e non attraversa il confine.
/// </para>
/// <para>
/// <b>Non è <see cref="LoadUnitSearchResult"/> con un nome diverso</b>, anche se ha la stessa forma.
/// Quella risponde «dove sta <i>questa</i> unità di carico», con l'unità di carico per chiave;
/// questa risponde «<i>quale</i> unità di carico ha questa referenza», con la referenza e il polmone
/// per chiave. Chiave diversa e semantica diversa: due operazioni, non una con un parametro in più.
/// </para>
/// <para>
/// L'esito viaggia nel corpo, non nello stato HTTP (come <see cref="StorageReservation"/>): «il
/// polmone non serve nessuna area» e «nessuna unità di carico ha quella referenza» sono risposte di
/// dominio, e il motore le distingue per lasciare all'operatore il messaggio giusto. Quando
/// <see cref="Outcome"/> non è <see cref="ReferenceSearchOutcome.Found"/> gli altri due campi sono
/// zero e non vanno letti.
/// </para>
/// </summary>
public sealed record ReferenceSearchResult(
    ReferenceSearchOutcome Outcome,
    decimal LoadUnitId,
    decimal CellId);
