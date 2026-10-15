namespace Wcs.Wms.Contracts;

/// <summary>
/// Fra le celle candidate a una destinazione di deposito (<see cref="ReserveStorageRequest"/>,
/// compito 12), se preferire quelle più piene o quelle più vuote. Gemello lato confine di
/// <c>WMS.Services.Interface.Enumeratori.TipiStatoCella</c>.
/// </summary>
public enum CellFullnessPreference : byte
{
    Fullest = 0,
    Emptiest = 1
}
