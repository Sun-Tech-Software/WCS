namespace Wcs.Engine.Persistence.Entities;

/// <summary>
/// Un comando in ingresso al motore, con il suo esito. Sostituisce <c>COMMAND_TODO</c> su SQL
/// Server.
/// </summary>
/// <remarks>
/// E' la porta d'ingresso dei comandi: fino alla tappa 3 del database del WCS la scrivono il
/// motore stesso (comandi automatici e comandi in errore) e chi gira nel suo processo; da fuori
/// entrera' dall'API. Corrispondenza con le vecchie colonne: <c>ID_COMMAND_TODO</c>→<c>Id</c>,
/// <c>COMMAND_TYPE</c>→<c>CommandType</c>, <c>ID_ITEM_COMMAND</c>→<c>ItemId</c>,
/// <c>ID_UDC</c>→<c>LoadUnitId</c>, <c>GUID_ASSEGNAZIONE_UDC</c>→<c>AssignmentGuid</c>,
/// <c>ID_WORKSTATION</c>→<c>WorkstationId</c>, <c>ID_LAVORAZIONE</c>→<c>ProcessId</c>,
/// <c>ID_BUFFER_CELL</c>→<c>BufferCellId</c>, <c>ID_LU_TYPE</c>→<c>LoadUnitTypeId</c>,
/// <c>ID_USER</c>→<c>UserId</c>, <c>ID_ITEM_DESTINATION</c>→<c>DestinationItemId</c>,
/// <c>ID_REFERENCE</c>→<c>ReferenceId</c>, <c>API</c>→<c>FromApi</c>, <c>API_STATUS</c>→<c>ApiStatus</c>.
/// </remarks>
public sealed class CommandRecord
{
    /// <summary>Chiave crescente: e' l'ordine di esecuzione, e le missioni la ricordano come <c>IdCommandToDo</c>.</summary>
    public long Id { get; set; }

    public DateTime Date { get; set; }
    public int CommandType { get; set; }
    public Guid? ItemId { get; set; }
    public decimal? LoadUnitId { get; set; }
    public string? StorageDoorCode { get; set; }
    public short Status { get; set; }
    public string? ErrorDescription { get; set; }
    public Guid? AssignmentGuid { get; set; }
    public string? Info { get; set; }
    public short ShowStatus { get; set; }
    public string? WorkstationId { get; set; }
    public decimal? ProcessId { get; set; }
    public decimal BufferCellId { get; set; }
    public string? PickUpDoorCode { get; set; }
    public decimal LoadUnitTypeId { get; set; }
    public string? UserId { get; set; }
    public Guid? DestinationItemId { get; set; }
    public string? ReferenceId { get; set; }
    public bool FromApi { get; set; }
    public int ApiStatus { get; set; }
}
