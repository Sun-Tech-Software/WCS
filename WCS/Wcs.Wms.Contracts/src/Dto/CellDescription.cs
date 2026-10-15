namespace Wcs.Wms.Contracts;

/// <summary>
/// Che cosa il motore sa di una cella. Sostituisce dieci letture sparse.
/// <see cref="DoorCodeA"/>, <see cref="DoorCodeB"/>, <see cref="DoorCount"/> e
/// <see cref="MultiDepthStorage"/> arrivano con il compito 7: senza di essi ogni loro lettura
/// resterebbe una chiamata a parte al gestionale.
/// </summary>
public sealed record CellDescription(
    decimal CellId,
    decimal AreaId,
    decimal WarehouseId,
    int Depth,
    bool Inverted,
    CellLockState Locked,
    decimal? AllowedLoadUnitTypeId,
    decimal? GroupId,
    int GroupOrder,
    string DoorCodeA,
    string DoorCodeB,
    int DoorCount,
    bool MultiDepthStorage);
