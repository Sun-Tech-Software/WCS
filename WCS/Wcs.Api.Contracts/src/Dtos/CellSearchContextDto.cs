using static Wcs.Common.Enums;

namespace Wcs.Api.Contracts.Dtos;

/// <summary>
/// Una cella servita da un magazzino automatico con ricollocatore, con quello che la ricerca celle
/// del WMS chiedeva a ITEMS_MAP e NAVIGATOR_ITEMS: il blocco al deposito (vero solo se tutte le
/// porte della cella sono bloccate) e lo stato dal vivo del ricollocatore (<c>OffLine</c> se il
/// motore non lo ha in memoria).
/// </summary>
public sealed record AutomaticCellDto(decimal CellId, Guid ItemId, Guid RelocatorItemId, bool StoringLocked, DeviceStatusTypes RelocatorStatus);

/// <summary>
/// La fotografia per una ricerca celle del WMS, presa in una lettura sola: celle automatiche,
/// celle con una riga di coda non lavorata, UDC in coda. Le tre liste sono quelle che le procedure
/// del WMS ricevono come elenchi di identificatori (`@lsCELLE_AUTOMATICHE`, `@lsCELLE_IN_ESTRAZIONE`,
/// `@lsUDC_IN_ESTRAZIONE`), costruiti dal WMS.Services in <c>ListeRicercaCelle</c>.
/// </summary>
public sealed record CellSearchContextDto(List<AutomaticCellDto> AutomaticCells, List<decimal> CellsInExtraction, List<decimal> LoadUnitsInExtraction);
