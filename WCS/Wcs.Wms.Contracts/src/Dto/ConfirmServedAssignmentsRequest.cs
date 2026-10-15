namespace Wcs.Wms.Contracts;

/// <summary>
/// La richiesta di <see cref="IWarehouse.ConfirmServedAssignmentsAsync"/> (compito 19): il motore
/// ha portato a destinazione l'unita' di carico di questa assegnazione, e le assegnazioni che ne
/// sono state servite vanno confermate.
/// </summary>
/// <remarks>
/// <b>Perche' la chiave e' l'assegnazione e non l'estrazione.</b> L'identificativo dell'estrazione
/// e' della coda del WCS, che il gestionale non vede: il motore lo risolve nella propria coda e
/// manda di qua l'unica cosa che il gestionale conosce. Da li' in poi tutto e' lavoro suo - trovare
/// le righe di distinta servite da quell'assegnazione, e confermarle una per una.
/// <para>
/// L'assegnazione compare anche in <see cref="LoadUnitLeftExtractionQueueRequest"/>, e le due non
/// si fondono: li' e' un campo di un fatto sull'unita' di carico che se ne va, qui e' la chiave di
/// un fatto sull'assegnazione che e' arrivata. Fatti diversi, e opposti - «non c'e' piu'» contro
/// «e' stata servita».
/// </para>
/// </remarks>
/// <param name="AssignmentId">L'assegnazione servita (<c>ASSEGNAZIONE_NOTE.ID_ASSEGNAZIONE_UDC</c>).</param>
/// <param name="UserId">
/// Chi conferma (<c>DISTINTA_NOTE.ID_UTENTE</c>): e' <c>Navigator.UserId</c>, l'identita' del WCS,
/// e viaggia nella richiesta perche' e' del WCS - il gestionale non la conosce. <b>Obbligatoria</b>,
/// come <see cref="ReportExtractionOutcomesRequest.WorkstationId"/>: dal motore un nullo non arriva
/// mai, e se ne arrivasse uno da un altro chiamante il buco si vedrebbe qui, al confine.
/// </param>
public sealed record ConfirmServedAssignmentsRequest(decimal AssignmentId, string UserId);
