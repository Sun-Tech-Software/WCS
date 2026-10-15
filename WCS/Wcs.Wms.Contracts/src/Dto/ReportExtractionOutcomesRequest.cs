namespace Wcs.Wms.Contracts;

/// <summary>
/// Il motore ha finito una testata di estrazione: ecco che cosa è successo a ciascuna delle sue
/// righe (compito 15, ex la scrittura di <c>DISTINTA_ESTRAZIONI_UDC</c> che il motore faceva da sé
/// con <c>SqlDistintaEstrazioniUdcRepository.UpsertAsync</c>).
/// </summary>
/// <remarks>
/// <b>Perché la chiave è la testata e non la riga.</b> Il criterio della casa - due punti d'accesso
/// stanno nella stessa operazione solo se condividono chiave, forma e semantica - qui porta alla
/// testata, non alla riga, e il motivo è la chiusura della lavorazione. Nel motore la chiusura non
/// era una scrittura per riga: girava <b>dopo</b> aver processato la testata, una volta, e girava
/// <b>anche quando non era stata scritta nessuna riga</b> - è per quella porta che una lavorazione
/// le cui righe sono tutte già risolte si chiude. Un'operazione con chiave la riga non saprebbe mai
/// dire «ho finito questa testata e non ho scritto niente», e quella lavorazione non si chiuderebbe
/// più. La chiusura in sé non è un'operazione di questo contratto: è una conseguenza che il
/// gestionale trae da solo, perché è lui a scrivere gli stati ed è lui a sapere quando il residuo va
/// a zero.
/// <para>
/// Un elenco di esiti vuoto è quindi legittimo e significativo, non una chiamata sprecata: dice che
/// il motore ha attraversato la testata senza scrivere nulla.
/// </para>
/// </remarks>
/// <param name="ExtractionHeaderId">
/// La testata (<c>ID_TESTATA_ESTRAZIONE_UDC</c>) di <see cref="PendingExtraction.ExtractionHeaderId"/>:
/// il gestionale ne risolve la lavorazione, che il motore non ha bisogno di conoscere.
/// </param>
/// <param name="WorkstationId">
/// La postazione che scrive (<c>DISTINTA_ESTRAZIONI_UDC.ID_UTENTE</c>): è
/// <c>Navigator.WorkstationId</c>, l'identità del WCS, e viaggia nella richiesta perché è del WCS -
/// il gestionale non la conosce. <b>Obbligatoria</b>: la colonna che scrive non ammette il nullo, e
/// <c>Navigator.WorkstationId</c> è a sua volta non annullabile, quindi dal motore un nullo non
/// arriva mai. Se ne arrivasse uno da un altro chiamante, il buco si vedrebbe qui, al confine, e non
/// più giù sul database.
/// </param>
/// <param name="Outcomes">Gli esiti, uno per riga servita; vuoto se il motore non ne ha scritto nessuno.</param>
public sealed record ReportExtractionOutcomesRequest(
    decimal ExtractionHeaderId,
    string WorkstationId,
    IReadOnlyList<ExtractionRowOutcome> Outcomes);
