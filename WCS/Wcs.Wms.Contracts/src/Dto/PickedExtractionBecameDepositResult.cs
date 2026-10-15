using System.Text.Json.Serialization;

namespace Wcs.Wms.Contracts;

/// <summary>
/// La risposta di <see cref="IWarehouse.ReportPickedExtractionBecameDepositAsync"/> (voce 45): un
/// esito per ciascuna delle due cose che il prelievo poteva aver servito. L'esito sta nel corpo di
/// un 200, come per <see cref="StorageReservation"/> e <see cref="ReferenceSearchResult"/>: «l'ordine
/// e' gia' chiuso» e «niente da riaprire» sono risposte, e una coda che le ricevesse come 4xx le
/// parcheggerebbe per sempre. Il 409 resta per lo stesso <c>OperationId</c> con un contenuto diverso.
/// </summary>
/// <remarks>
/// <b>I due esiti sono obbligatori sul filo</b> (<see cref="JsonRequiredAttribute"/>). Il valore 0 e'
/// <see cref="PickedExtractionReopenOutcome.Reopened"/>: senza l'obbligo un 200 con un corpo vuoto o
/// monco si leggerebbe «riaperto», e il WCS toglierebbe dalla coda un fatto che il gestionale non ha
/// mai applicato. Con l'obbligo e' un corpo che non si legge, cioe' un guasto, e si ritenta.
/// </remarks>
/// <param name="Assignment">L'esito per l'assegnazione di nota (<c>AssignmentId</c> della richiesta).</param>
/// <param name="ExtractionRow">L'esito per la riga di estrazione per unita' di carico.</param>
/// <param name="Detail">
/// Il perche', per il registro del WCS: quale condizione ha chiuso l'ordine, o perche' non c'era
/// niente da riaprire. Nullo quando non c'e' niente da aggiungere.
/// </param>
public sealed record PickedExtractionBecameDepositResult(
    [property: JsonRequired] PickedExtractionReopenOutcome Assignment,
    [property: JsonRequired] PickedExtractionReopenOutcome ExtractionRow,
    string? Detail);
