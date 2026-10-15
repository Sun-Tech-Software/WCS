namespace Wcs.Wms.Contracts;

/// <summary>
/// Il filtro sul blocco dell'unità di carico per <see cref="FindLoadUnitRequest"/> (compito 11).
/// Gemello lato confine di <c>WMS.Services.Interface.Enumeratori.TipiRicercheBloccoUdc</c>, che resta
/// nel gestionale per i suoi chiamanti che non passano da qui (l'API di <c>OperazioniUDCController</c>).
/// </summary>
public enum LoadUnitLockFilter : byte
{
    Any = 0,
    Locked = 1,
    Unlocked = 2
}
