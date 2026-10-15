namespace Wcs.Wms.Contracts;

/// <summary>
/// L'area va deframmentata per questo tipo di unita' di carico (compito 4)? E' un <b>verdetto</b>,
/// non i numeri da cui si ricava: quante celle ha l'area, quante ne sono occupate e quante ne
/// tengono una sola unita' di carico restano di la' dal confine, con la soglia che le legge.
/// <para>
/// <b>La politica e' del gestionale.</b> Oggi dice di si' se piu' dell'85 % delle celle dell'area e'
/// occupato, oppure se piu' del 10 % tiene una sola unita' di carico; domani potrebbe dire altro, e
/// il motore non se ne accorgerebbe - riceve si' o no e ne trae una cosa sola: la cella appena
/// riempita e' candidata alla compattazione.
/// </para>
/// </summary>
/// <remarks>
/// La chiave e' la <b>coppia</b> area piu' tipo di unita' di carico: la stessa area risponde
/// diversamente per due tipi, perche' le celle si contano per tipo. Per questo il tipo e' nella
/// domanda e non un contorno, e viaggia nella query string della rotta.
/// </remarks>
public sealed record AreaDefragmentationVerdict(decimal AreaId, decimal LoadUnitTypeId, bool Needed);
