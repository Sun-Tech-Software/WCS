namespace Wcs.Wms.Contracts;

/// <summary>
/// Quale unità di carico ha questa referenza, fra quelle che si possono prelevare per questo
/// polmone (compito 20, ex <c>CelleMagazzino.TrovaReferenza</c> chiamata dal motore).
/// <para>
/// <b>Due campi soltanto, ed è il punto dell'operazione.</b> Il motore costruiva da sé la richiesta
/// del gestionale: le opzioni di ricerca (aree selezionabili, tipo di operatore, celle da cercare,
/// lista delle aree), la richiesta di referenza con la sua lista di attributi vuota, il numero di
/// risultati e il tipo di ricerca - tutti valori fissi, che però lo obbligavano a conoscere il
/// modello di ricerca del gestionale. Sono rimasti i due che il motore sa davvero: la referenza
/// chiesta dall'operatore e il polmone da cui si preleva. Il resto - comprese le aree servite dal
/// polmone, che il motore leggeva da sé su <c>AREE_PRELIEVO_PICKING</c> - si formula dall'altra
/// parte, dove le tabelle sono.
/// </para>
/// </summary>
/// <param name="ReferenceId">La referenza da cercare, il codice che l'operatore ha chiesto.</param>
/// <param name="BufferCellId">La cella polmone su cui la referenza deve arrivare.</param>
public sealed record FindLoadUnitByReferenceRequest(
    string ReferenceId,
    decimal BufferCellId);
