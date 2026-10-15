namespace Wcs.Wms.Contracts;

/// <summary>
/// Che cosa ha fatto il gestionale di una delle due cose che un'estrazione gia' prelevata aveva
/// servito, quando e' diventata un deposito (voce 45, <see cref="PickedExtractionBecameDepositResult"/>).
/// Tre esiti, e <b>tutti e tre sono risposte di dominio</b>: viaggiano nel corpo di un 200, e chi
/// manda la chiamata da una coda la toglie dalla coda per ciascuno dei tre. Solo un guasto si
/// ritenta, e un guasto non e' un esito.
/// </summary>
/// <remarks>
/// I valori sono scritti a mano perche' viaggiano sul filo come numeri: il gestionale li serializza
/// cosi', e un riordino dei nomi cambierebbe il significato di una risposta senza cambiarne la forma.
/// </remarks>
public enum PickedExtractionReopenOutcome : byte
{
    /// <summary>Il gestionale ha riaperto: la riga torna da servire.</summary>
    Reopened = 0,

    /// <summary>
    /// Non c'era niente da riaprire: la missione non aveva servito niente di questo tipo, la
    /// conferma non era arrivata, la traccia della conferma manca (conferme anteriori alla voce 45),
    /// o il tipo di nota non si riattiva (<c>NON_RIATTIVABILE</c>, con una riga di avviso nel
    /// registro del gestionale).
    /// </summary>
    NothingToReopen = 1,

    /// <summary>
    /// L'ordine e' gia' chiuso - nota annullata, spedizione spedita o in conferma, unita' di carico
    /// spedita, testata o lavorazione archiviate - e il gestionale non tocca niente. Il dettaglio
    /// del risultato dice quale delle condizioni l'ha fermato.
    /// </summary>
    OrderAlreadyClosed = 2
}
