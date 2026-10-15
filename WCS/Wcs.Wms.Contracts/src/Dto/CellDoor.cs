namespace Wcs.Wms.Contracts;

/// <summary>
/// Le due porte fisiche di una cella. Nasce con il compito 9 per <see cref="CellStoringConstraints"/>:
/// oggi solo la porta A ha un chiamante reale nel motore (la porta B esiste nel contratto perché la
/// query del gestionale si comporta diversamente a seconda della porta scelta, non perché qualcuno la
/// chieda ancora).
/// </summary>
public enum CellDoor : byte
{
    A = 0,
    B = 1
}
