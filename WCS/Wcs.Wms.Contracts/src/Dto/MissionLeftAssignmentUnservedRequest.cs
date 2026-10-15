namespace Wcs.Wms.Contracts;

/// <summary>
/// La segnalazione di <see cref="IWarehouse.ReportMissionLeftAssignmentUnservedAsync"/> (compito 21):
/// la missione che portava un'assegnazione e' uscita dal motore senza servirla.
/// </summary>
/// <remarks>
/// <b>La chiave e' la missione, non l'assegnazione.</b> E' la ragione per cui i punti del motore
/// che segnalano questo fatto stanno in una sola operazione benche' non nominino l'assegnazione allo
/// stesso modo: la missione e' una, e i due identificativi qui sotto sono due <i>campi</i> suoi, non
/// due chiavi di due fatti diversi.
/// <para>
/// <b>I due identificativi non sono lo stesso numero scritto in due modi.</b> Nel gestionale
/// <c>ASSEGNAZIONE_NOTE.GUID_ASSEGNAZIONE_UDC</c> nomina l'assegnazione <b>intera</b> di un'unita' di
/// carico (tutte le righe nate dallo stesso giro di assegnazione, una per riga di nota), mentre
/// <c>ID_ASSEGNAZIONE_UDC</c> nomina <b>una</b> di quelle righe: cancellare per l'uno o per l'altro
/// non tocca lo stesso insieme di righe. La missione porta l'uno o l'altro secondo come e' nata - il
/// GUID quando l'ha ricevuto dalla riga di coda di estrazione, l'identificativo di riga quando e'
/// stata ricaricata dal database del WCS - e la segnalazione porta quello che la missione ha. Il
/// gestionale disfa quello che riceve, e non deve indovinare l'altro.
/// </para>
/// <para>
/// <b>Perche' «non servita».</b> Quando l'unita' di carico arriva a destinazione e l'assegnazione
/// viene consumata, il motore azzera <c>GuidAssignmentLu</c> nello stesso giro
/// (<see cref="MissionServedAssignmentRequest"/>): la missione che si chiude dopo non ha piu' niente
/// da segnalare. Quello che arriva qui e' percio' sempre un'assegnazione che nessuno ha servito, e
/// il fatto opposto ha un'operazione sua.
/// </para>
/// </remarks>
/// <param name="MissionId">La missione uscita dal motore.</param>
/// <param name="LoadUnitId">L'unita' di carico che portava: serve al registro, non alla cancellazione.</param>
/// <param name="AssignmentGuid">
/// L'assegnazione intera dell'unita' di carico (<c>GUID_ASSEGNAZIONE_UDC</c>), oppure
/// <see cref="System.Guid.Empty"/> se la missione non la nomina cosi'.
/// </param>
/// <param name="AssignmentId">
/// La riga di assegnazione (<c>ID_ASSEGNAZIONE_UDC</c>), oppure <c>0</c> se la missione non la nomina
/// cosi'. Almeno uno dei due identificativi e' valorizzato: una missione senza assegnazione non
/// produce questa segnalazione.
/// </param>
/// <param name="Reason">
/// Perche' la missione se n'e' andata: e' quello che un operatore legge quando un'assegnazione
/// sparisce. Il gestionale lo scrive nel proprio registro accanto alla cancellazione.
/// </param>
public sealed record MissionLeftAssignmentUnservedRequest(
    Guid MissionId,
    decimal LoadUnitId,
    Guid AssignmentGuid,
    decimal AssignmentId,
    string Reason);
