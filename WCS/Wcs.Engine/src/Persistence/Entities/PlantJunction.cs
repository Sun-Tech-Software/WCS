namespace Wcs.Engine.Persistence.Entities;

/// <summary>
/// Un collegamento orientato fra due dispositivi del diagramma, con la sua priorita'. Sostituisce
/// <c>NAVIGATOR_JUNCTIONS</c> su SQL Server.
/// </summary>
public sealed class PlantJunction
{
    public Guid JunctionId { get; set; }
    public Guid SourceItemId { get; set; }
    public Guid DestinationItemId { get; set; }
    public int Priority { get; set; }
}
