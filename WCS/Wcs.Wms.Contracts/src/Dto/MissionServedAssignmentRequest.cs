namespace Wcs.Wms.Contracts;

/// <summary>
/// La segnalazione di <see cref="IWarehouse.ReportMissionServedAssignmentAsync"/> (compito 21):
/// l'unita' di carico e' arrivata alla postazione e l'assegnazione che la missione portava e' stata
/// servita.
/// </summary>
/// <remarks>
/// <b>Non e' <see cref="MissionLeftAssignmentUnservedRequest"/> con un campo in meno.</b> La chiave e'
/// la stessa (la missione) e la forma quasi la stessa, ma la semantica e' l'opposto: li'
/// l'assegnazione non e' stata servita e la nota deve poter tornare assegnabile, qui e' stata servita
/// e le sue righe sono gia' state marcate prelevate. Due punti d'accesso stanno nella stessa
/// operazione solo se condividono chiave, forma <b>e</b> semantica; oggi il gestionale trae da tutt'e
/// due la stessa conseguenza - le righe di assegnazione si cancellano - ma e' proprio la conseguenza
/// che qui puo' cambiare da sola, ed e' il motivo per cui i due fatti viaggiano separati.
/// </remarks>
/// <param name="MissionId">La missione che ha servito l'assegnazione.</param>
/// <param name="LoadUnitId">L'unita' di carico arrivata: serve al registro, non alla cancellazione.</param>
/// <param name="AssignmentGuid">
/// L'assegnazione intera dell'unita' di carico (<c>GUID_ASSEGNAZIONE_UDC</c>): qui c'e' sempre, ed e'
/// la sola forma con cui il motore nomina l'assegnazione su questo percorso.
/// </param>
/// <param name="Reason">
/// Che cosa e' successo, per il registro del gestionale: la riga che un operatore legge quando
/// un'assegnazione sparisce non deve mancare nemmeno quando la sparizione e' quella normale.
/// </param>
public sealed record MissionServedAssignmentRequest(
    Guid MissionId,
    decimal LoadUnitId,
    Guid AssignmentGuid,
    string Reason);
