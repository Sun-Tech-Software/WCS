namespace Wcs.Wms.Contracts;

/// <summary>
/// Le partizioni della cella, con l'offset di ciascuna, e le colonne ASI/SUBITEM delle sue due
/// porte. Sostituisce tre letture (compito 8) che leggevano la stessa tabella (PARTIZIONI_AREE) con
/// chiavi diverse: l'indirizzo di campo per codice porta, la profondità della cella per una
/// partizione data, e la profondità di stoccaggio per un indirizzo ASI. <see cref="Partitions"/>
/// risponde a tutt'e tre senza una seconda chiamata: chi cerca un offset lo cerca per nome di
/// partizione, chi cerca un nome lo cerca per offset.
/// <para>
/// Un indirizzo <b>vuoto</b> significa che quella porta non ha un indirizzo di campo - il caso
/// normale della porta B su una cella con una porta sola, non un guasto ne' un dato mancante. Il
/// gestionale legge come vuote sia la colonna vuota sia quella nulla, che nello schema sono due
/// scritture dello stesso fatto (vedi <c>MagazzinoPerWcs.ReadCellDoors</c>).
/// </para>
/// </summary>
public sealed record CellPartitionMap(
    decimal CellId,
    string AsiDoorA,
    string SubitemDoorA,
    string AsiDoorB,
    string SubitemDoorB,
    IReadOnlyList<CellPartition> Partitions);
