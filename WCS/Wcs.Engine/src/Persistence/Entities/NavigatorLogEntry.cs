namespace Wcs.Engine.Persistence.Entities;

/// <summary>
/// Una riga del registro del navigatore: che cosa e' successo, a quale dispositivo, per quale
/// missione. Sta nello storico mensile. Sostituisce <c>NAVIGATOR_LOG</c> su SQL Server.
/// </summary>
public sealed class NavigatorLogEntry
{
    public long Id { get; set; }
    public DateTime Date { get; set; }
    public Guid? ItemId { get; set; }
    public string? Asi { get; set; }
    public string? Partition { get; set; }
    public string? Subitem { get; set; }
    public short Status { get; set; }
    public string? Description { get; set; }
    public Guid? DestinationItemId { get; set; }
    public Guid? MissionId { get; set; }
    public DateTime? MissionStart { get; set; }
    public DateTime? MissionEnd { get; set; }
    public short MissionStatus { get; set; }
    public Guid? RunningItemId { get; set; }
}
