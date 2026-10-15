namespace Wcs.Wms.Contracts;

/// <summary>
/// A quale profondità si può depositare in questa cella (compito 9): la quota teorica calcolata dal
/// gestionale tenendo conto delle unità di carico già presenti. Sostituisce <c>QuotaDepositoUdc</c>:
/// degli altri due campi che il gestionale calcola insieme a questo (la compattazione da eseguire e
/// la partizione prevista) nessun chiamante del motore legge oggi, quindi non sono qui — se un
/// compito futuro ne avrà bisogno, si aggiungono allora.
/// </summary>
public sealed record CellStoringConstraints(int TheoreticalStoreDepth);
