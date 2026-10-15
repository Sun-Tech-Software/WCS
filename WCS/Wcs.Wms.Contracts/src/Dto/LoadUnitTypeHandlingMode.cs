namespace Wcs.Wms.Contracts;

/// <summary>
/// La modalita' di movimentazione di un <b>tipo</b> di unita' di carico (<c>TIPO_UDC.HANDLING_MODE</c>).
/// Chiave il tipo, non l'UDC: serve ai punti che impostano il tipo senza avere un'UDC reale
/// (la costruzione di una missione di prova, la lettura del tipo dal campo). Per un'UDC che esiste
/// c'e' <see cref="LoadUnitDescription.HandlingMode"/>.
/// </summary>
public sealed record LoadUnitTypeHandlingMode(decimal LoadUnitTypeId, int HandlingMode);
