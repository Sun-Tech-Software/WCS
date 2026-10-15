namespace Wcs.Wms.Contracts;

/// <summary>
/// Il filtro sul blocco della cella per <see cref="FindLoadUnitRequest"/> (compito 11). Gemello lato
/// confine di <c>WMS.Services.Interface.Enumeratori.TipiRicercheBloccoCelle</c>, che resta nel
/// gestionale per i suoi chiamanti che non passano da qui (l'API di <c>OperazioniUDCController</c>).
/// Non è lo stesso concetto di <see cref="CellLockState"/>: quello è lo stato scritto sulla cella,
/// questo è un criterio di ricerca («libera per lo scaricamento» accetta due stati diversi).
/// </summary>
public enum CellLockFilter : byte
{
    Any = 0,
    Loading = 1,
    Unloading = 2,
    Locked = 3,
    Unlocked = 4,
    FreeForLoading = 5,
    FreeForUnloading = 6
}
