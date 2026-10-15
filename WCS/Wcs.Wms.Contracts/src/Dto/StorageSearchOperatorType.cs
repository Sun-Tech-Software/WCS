namespace Wcs.Wms.Contracts;

/// <summary>
/// Chi effettua la ricerca di una destinazione di deposito (<see cref="ReserveStorageRequest"/>,
/// compito 12): un operatore manuale o uno automatico - influisce su come il gestionale ordina e
/// filtra le celle candidate. Gemello lato confine di
/// <c>WMS.Services.Interface.Enumeratori.TipiOperatoreRicerca</c>.
/// </summary>
public enum StorageSearchOperatorType : byte
{
    Manual = 0,
    Automatic = 1
}
