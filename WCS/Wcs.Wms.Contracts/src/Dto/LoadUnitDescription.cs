namespace Wcs.Wms.Contracts;

/// <summary>
/// Che cosa c'e' scritto su questa unita' di carico: codice, tipo, modalita' di movimentazione,
/// tara e dimensioni (compito 10). Sostituisce tre letture sparse sulla stessa riga di <c>UDC</c>
/// (il codice, i dati dimensionali, la modalita' di movimentazione del tipo) con una sola domanda,
/// chiave <paramref name="LoadUnitId"/> in tutti e tre i casi - la stessa condizione per cui il
/// compito 5 ha unito le letture di una cella. Null significa «questa unita' di carico non esiste»,
/// una risposta di dominio per chi la usa per verificare l'esistenza (non un guasto): il client
/// traduce il 404 in null, come <see cref="LoadUnitLocation"/>.
/// </summary>
public sealed record LoadUnitDescription(
    decimal LoadUnitId,
    string Code,
    decimal LoadUnitTypeId,
    decimal HandlingMode,
    int Tare,
    int Height,
    int Width,
    int Depth,
    int Weight);
