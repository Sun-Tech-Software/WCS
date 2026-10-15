namespace Wcs.Wms.Contracts;

/// <summary>
/// La porta di prelievo scelta per un'unità di carico in una cella (compito 11, ex
/// <c>Navigator.PickUpDoor</c> lato motore): su una cella a più porte, quella con meno ostacoli
/// davanti; a parità (o con una sola porta), quella d'ingresso. Il conteggio e l'elenco arrivano
/// insieme perché il motore li usa insieme, come <c>ObstacleReport</c> che questa operazione sostituisce -
/// quello aveva la chiave sbagliata (solo l'unità di carico): non poteva esprimere il confronto fra
/// le due porte che <see cref="Door"/> qui risolve.
/// </summary>
public sealed record PickUpDoorChoice(
    CellDoor Door,
    string? DoorCode,
    int ObstacleCount,
    IReadOnlyList<decimal> ObstacleLoadUnitIds);
