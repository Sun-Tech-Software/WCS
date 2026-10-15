namespace Wcs.Wms.Contracts;

/// <summary>
/// Una cella come la vede l'Editor del WCS quando collega porte e polmoni all'impianto (compito
/// 7): la descrizione del magazzino e dell'area, la posizione nella scaffalatura, i due codici
/// porta e la descrizione. E' un <b>elenco da scegliere</b>, non un dato che il motore usa per
/// decidere: nessuno di questi campi entra in una missione.
/// </summary>
/// <remarks>
/// <para>
/// Gli otto campi sono quelli di <c>WmsCellRow</c>, il tipo che l'host costruiva da se' leggendo
/// <c>CELLE_AREE</c>, <c>AREE_MAGAZZINO</c> e <c>MAGAZZINI</c> in T-SQL diretto: la finestra
/// dell'Editor non cambia, quindi non cambia nemmeno la forma. Cambiano i nomi di due campi -
/// <c>Warehouse</c>/<c>Area</c> diventano <see cref="WarehouseDescription"/>/<see cref="AreaDescription"/>
/// - perche' sono descrizioni e non identificativi, e su un confine il nome deve dire quale delle
/// due cose viaggia. Il DTO dell'API del WCS (<c>WmsCell</c>) resta com'e': quello lo legge
/// l'Editor.
/// </para>
/// <para>
/// <b>Non e' <see cref="CellDescription"/>.</b> Quella e' la cella su cui il motore lavora -
/// profondita', porte, stato di blocco, area - e la si chiede per identificativo; questa e' una
/// riga di un elenco ordinato per magazzino, area, colonna e piano, e la si chiede per tipo
/// d'area. Chiave diversa, forma diversa, semantica diversa: due operazioni, non una con
/// un'opzione.
/// </para>
/// <para>
/// <see cref="Floor"/>, <see cref="DoorCodeA"/>, <see cref="DoorCodeB"/> e
/// <see cref="Description"/> sono annullabili qui benche' sullo schema di DEMOPLANT_WMS
/// solo <c>CELLE_AREE.DESCRIZIONE</c> lo sia (verificato): e' la forma che l'Editor gia' riceve, e
/// il compito sposta una lettura senza restringerla. Un impianto con un'altra versione dello
/// schema si vedrebbe rifiutare la riga, non riempire di zeri.
/// </para>
/// </remarks>
public sealed record EditorCellRow(
    decimal CellId,
    string WarehouseDescription,
    string AreaDescription,
    int Column,
    int? Floor,
    string? DoorCodeA,
    string? DoorCodeB,
    string? Description);
