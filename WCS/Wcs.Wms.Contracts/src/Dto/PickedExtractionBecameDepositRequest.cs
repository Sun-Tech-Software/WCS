namespace Wcs.Wms.Contracts;

/// <summary>
/// La segnalazione di <see cref="IWarehouse.ReportPickedExtractionBecameDepositAsync"/> (voce 45):
/// l'estrazione che questa missione aveva gia' prelevato non arrivera' alla postazione, perche'
/// l'operatore l'ha mandata in un magazzino ed e' diventata un deposito. Il fatto e' del WCS; la
/// conseguenza - riaprire quello che il prelievo aveva dato per servito - e' del gestionale.
/// Porta anche il cambio cella prima del prelievo (voce 45, compito 4): l'estrazione con l'unita' di
/// carico ancora nella sua cella, mandata in un altro magazzino, con la sola riga di estrazione.
/// </summary>
/// <remarks>
/// <b>Il nome dice il fatto, non l'ordine</b> («riapri»), come
/// <see cref="MissionLeftAssignmentUnservedRequest"/>. I tre campi di che cosa e' stato servito li
/// ricorda la missione dal prelievo (<c>ServedAssignmentId</c>, <c>ServedAssignmentGuid</c>,
/// <c>ServedExtractionRowId</c>): al momento del cambio la riga di coda e il GUID dell'assegnazione
/// non ci sono piu'. Ciascuno dei tre puo' mancare - una missione di nota non ha riga di estrazione,
/// una di estrazione per unita' di carico non ha assegnazione - e la mancanza ha il suo valore
/// neutro, non un nullo.
/// <para>
/// <b>Il cambio cella prima del prelievo</b>: <see cref="AssignmentId"/> 0 e
/// <see cref="AssignmentGuid"/> vuoto (l'assegnazione l'hanno gia' lasciata le segnalazioni del
/// cambio cella), <see cref="ExtractionRowId"/> la riga della coda di estrazione del WCS che si
/// cancella. Quella riga non e' stata segnata estratta - il prelievo non c'e' stato - ma il
/// gestionale l'aveva messa «in estrazione»: va rimessa da servire lo stesso.
/// </para>
/// </remarks>
/// <param name="OperationId">
/// L'identita' stabile del fatto, la stessa a ogni tentativo: e' la chiave della ricevuta del
/// gestionale (<c>WCS_WAREHOUSE_TRANSITION_RECEIPTS</c>). Qui la ricevuta non e' facoltativa - una
/// ripetizione tardiva senza ricevuta cancellerebbe di nuovo una riga di estrazione gia' rifatta e
/// riprelevata - quindi a differenza di <see cref="AllocateLoadUnitRequest.OperationId"/> non c'e'
/// un chiamante legacy con <see cref="Guid.Empty"/>.
/// </param>
/// <param name="MissionId">La missione che aveva prelevato, o che e' diventata un cambio cella: serve al registro.</param>
/// <param name="LoadUnitId">L'unita' di carico, che adesso va in magazzino.</param>
/// <param name="AssignmentId">
/// L'assegnazione di nota confermata al prelievo (<c>ID_ASSEGNAZIONE_UDC</c>); 0 se nessuna.
/// </param>
/// <param name="AssignmentGuid">
/// L'assegnazione intera segnalata come servita (<c>GUID_ASSEGNAZIONE_UDC</c>); <see cref="Guid.Empty"/>
/// se nessuna. Serve al registro: le righe che la segnalazione «servita» ha cancellato senza
/// conferma sono gia' tornate nel residuo da sole.
/// </param>
/// <param name="ExtractionRowId">
/// La riga di estrazione per unita' di carico segnata estratta al prelievo
/// (<c>ID_RIGA_ESTRAZIONE_UDC</c>), o nel cambio cella prima del prelievo quella che la coda di
/// estrazione del WCS stava servendo, ancora «in estrazione»; 0 se nessuna.
/// </param>
/// <param name="UserId">
/// Chi segnala: e' <c>Navigator.UserId</c>, l'identita' del WCS, come in
/// <see cref="ConfirmServedAssignmentsRequest.UserId"/>.
/// </param>
/// <param name="Reason">Che cosa e' successo, per il registro del gestionale.</param>
public sealed record PickedExtractionBecameDepositRequest(
    Guid OperationId,
    Guid MissionId,
    decimal LoadUnitId,
    decimal AssignmentId,
    Guid AssignmentGuid,
    decimal ExtractionRowId,
    string UserId,
    string Reason);
