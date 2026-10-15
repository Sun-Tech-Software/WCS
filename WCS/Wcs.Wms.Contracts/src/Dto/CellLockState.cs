namespace Wcs.Wms.Contracts;

/// <summary>
/// Lo stato di blocco di una cella: quattro valori, non un booleano. Il deposito e' bloccato
/// quando lo stato e' <see cref="Full"/> o <see cref="Loading"/>, e consentito quando e'
/// <see cref="None"/> o <see cref="Unloading"/> - una cella bloccata solo al prelievo resta
/// valida per il deposito, distinzione che un booleano non potrebbe esprimere.
/// Stessi valori di <c>Wcs.Common.Enums.CellLockTypes</c>, l'enumerazione gemella lato motore: un
/// test li tiene allineati, perche' due enum che scivolano l'uno rispetto all'altro non
/// fallirebbero mai a compilazione.
/// </summary>
public enum CellLockState : byte
{
    None = 0,
    Loading = 1,
    Unloading = 2,
    Full = 3
}
