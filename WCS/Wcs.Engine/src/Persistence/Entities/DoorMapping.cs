namespace Wcs.Engine.Persistence.Entities;

/// <summary>
/// Il collegamento fra una cella del magazzino, il codice porta con cui il campo la chiama e il
/// dispositivo del diagramma che la serve, con i due stati che il motore vi scrive. Sostituisce
/// <c>ITEMS_MAP</c> su SQL Server.
/// </summary>
/// <remarks>
/// La scrive l'Editor quando collega un dispositivo alle celle; il motore la legge per passare da
/// porta a cella a dispositivo e scrive i due stati: <see cref="StoringLocked"/> (la cella e'
/// impegnata per un deposito) e <see cref="OperationActive"/> (l'operazione in corso sulla cella).
/// Corrispondenza con le vecchie colonne: <c>ID_ITEM</c>→<c>ItemId</c>, <c>DOOR_CODE</c>→<c>DoorCode</c>,
/// <c>ID_CELLA</c>→<c>CellId</c>, <c>CODE</c>→<c>Side</c> (la porta A o B), <c>OPERATION_ACTIVE</c>→<c>OperationActive</c>,
/// <c>STORING_LOCKED</c>→<c>StoringLocked</c>. Per decisione del responsabile del prodotto (2026-09-02) sta
/// nel WCS ed e' esposta via API: le query del WMS che la leggevano su SQL Server aspettano l'API.
/// </remarks>
public sealed class DoorMapping
{
    public long Id { get; set; }
    public Guid ItemId { get; set; }
    public string? DoorCode { get; set; }
    public decimal CellId { get; set; }
    public string? Side { get; set; }
    public short OperationActive { get; set; }
    public bool StoringLocked { get; set; }
}
