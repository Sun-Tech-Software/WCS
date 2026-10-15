namespace Wcs.Wms.Contracts;

/// <summary>
/// Se la ricerca di una destinazione di deposito (<see cref="ReserveStorageRequest"/>, compito 12)
/// esplora tutte le aree ammesse o si ferma alla prima che risponde. Gemello lato confine di
/// <c>WMS.Services.Interface.Enumeratori.TipiAreeSelezionabili</c>.
/// </summary>
public enum StorageAreaSelectionMode : byte
{
    All = 0,
    First = 1
}
