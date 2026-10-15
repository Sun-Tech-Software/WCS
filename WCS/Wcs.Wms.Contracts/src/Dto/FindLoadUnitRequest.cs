namespace Wcs.Wms.Contracts;

/// <summary>
/// Dove cercare un'unità di carico (compito 11, ex <c>DbInfo.OperazioniUdc.TrovaUdc</c>): fra le celle
/// di <paramref name="AllowedAreaIds"/>, oppure fra quelle di <paramref name="AllowedCellIds"/> - mai
/// insieme, come l'originale: se <paramref name="AllowedAreaIds"/> non è vuoto vince lui, altrimenti
/// si guarda <paramref name="AllowedCellIds"/>. Una lista di celle vuota (non nulla) è una domanda
/// legittima che risponde sempre «non trovata»: nessuna cella è ammessa, non «tutte».
/// </summary>
public sealed record FindLoadUnitRequest(
    decimal LoadUnitId,
    IReadOnlyList<decimal>? AllowedAreaIds,
    IReadOnlyList<decimal>? AllowedCellIds,
    LoadUnitLockFilter LoadUnitLock,
    CellLockFilter CellLock);
