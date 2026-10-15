namespace Wcs.Wms.Contracts;

/// <summary>
/// Il motore scarta un'assegnazione di nota di spedizione che non può servire, e dice perché.
/// È il quarto motivo di rifiuto - l'unico che il gestionale non può applicare da sé, perché per
/// scoprirlo bisogna cercare l'unità di carico nel vivo del diagramma - e da qui in poi segue la
/// strada degli altri tre: il gestionale cancella l'assegnazione e scrive il motivo nel proprio
/// registro, così potrà assegnarne un'altra al giro successivo.
/// </summary>
/// <remarks>
/// Non è nessuno dei tre fatti del compito 21 (<see cref="MissionLeftAssignmentUnservedRequest"/>,
/// <see cref="MissionServedAssignmentRequest"/>, <see cref="UnservableQueuedExtractionRequest"/>),
/// che raccolgono i sei punti in cui il motore annullava un'assegnazione di unità di carico: quelli
/// nascono dal ciclo di vita di una <b>missione</b>, e qui una missione non se ne va mai. Due punti
/// d'accesso stanno nella stessa operazione solo se condividono chiave, forma e semantica.
/// <para>
/// <b>Dal compito 13 i punti sono cinque.</b> Oltre al quarto motivo di rifiuto di
/// <c>GetPendingNoteWork</c>, ci sono i quattro in cui una riga della coda di estrazione del WCS se
/// ne va e l'assegnazione che portava non ha più chi la serva: i due del ciclo di sfondo del
/// magazzino e i due delle pulizie periodiche della coda, che prima la cancellavano da sé con un
/// <c>DELETE</c> su <c>ASSEGNAZIONE_NOTE</c>. Condividono tutt'e tre le cose, e quello che li
/// distingue - il motivo - è un campo, non un'operazione.
/// </para>
/// <para>
/// <b>Perché non <see cref="UnservableQueuedExtractionRequest"/></b>, che di quei quattro
/// descriverebbe bene la scena: quella nomina l'assegnazione con il <b>GUID</b>, cioè l'insieme di
/// tutte le righe nate dallo stesso giro di assegnazione dell'unità di carico, mentre qui la chiave
/// è la <b>riga</b>, che è esattamente quella che il <c>DELETE</c> di prima toglieva. Chiave
/// diversa, insieme di righe diverso.
/// </para>
/// </remarks>
/// <param name="AssignmentId">L'assegnazione da togliere (<c>ID_ASSEGNAZIONE_UDC</c>).</param>
/// <param name="LoadUnitId">L'unità di carico che l'assegnazione indicava: serve al registro, non alla cancellazione.</param>
/// <param name="Reason">Perché non può essere servita: è quello che un operatore legge quando una spedizione non parte.</param>
public sealed record RejectNoteAssignmentRequest(
    decimal AssignmentId,
    decimal LoadUnitId,
    string Reason);
