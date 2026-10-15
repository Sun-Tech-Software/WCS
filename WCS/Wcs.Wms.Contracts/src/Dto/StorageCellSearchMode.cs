namespace Wcs.Wms.Contracts;

/// <summary>
/// Quali celle ammette la ricerca di una destinazione di deposito (<see cref="ReserveStorageRequest"/>,
/// compito 12): tutte, solo quelle libere per un operatore automatico, solo quelle collegate a un
/// operatore automatico, o solo quelle raggiungibili da uno degli operatori indicati in
/// <see cref="ReserveStorageRequest.ReachableDeviceIds"/>. Gemello lato confine di
/// <c>WMS.Services.Interface.Enumeratori.TipiCelleRicercare</c>.
/// </summary>
public enum StorageCellSearchMode : byte
{
    All = 0,
    FreeAutomaticOperator = 1,
    ConnectedAutomaticOperator = 2,
    CellsReachableByAutomaticOperator = 3
}
