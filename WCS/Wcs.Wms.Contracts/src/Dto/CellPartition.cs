namespace Wcs.Wms.Contracts;

/// <summary>Una riga della tabella delle partizioni dell'area: il nome e l'offset (la profondità).</summary>
public sealed record CellPartition(string Name, int Offset);
