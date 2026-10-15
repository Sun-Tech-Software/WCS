namespace Wcs.Wms.Contracts;

/// <summary>
/// L'occupazione di una cella non è un booleano (compito 9): il motore distingue occupata per
/// estrazione (un'assegnazione del WMS insiste sulla cella), per prelievo (un'assegnazione di note
/// in attesa, un'unità di carico esclusa) e per deposito (una posizione già assegnata, un'unità di
/// carico esclusa) — le tre cose portano a decisioni diverse. Ridurle a un sì/no cancellerebbe la
/// distinzione: un compito precedente del piano si è fermato apposta per non farlo.
/// </summary>
public sealed record CellBusyState(bool Extraction, bool PickUp, bool Storing);
