using Wcs.Common;

namespace Wcs.Engine.Items;

#region Public Classes

public class NavigatorLogRecord
{
    #region Public Properties

    public string? ASI { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public Guid? DestinationItemId { get; set; } = Guid.Empty;
    public DateTime? EndMission { get; set; }
    public Guid? MissionId { get; set; } = Guid.Empty;
    public string? PARTITION { get; set; } = string.Empty;
    public Guid? RunningItemId { get; set; } = Guid.Empty;
    public Guid? SourceItemId { get; set; } = Guid.Empty;
    public DateTime? StartMission { get; set; }
    public byte Status { get; set; }
    public Enums.ResultCodeTypes StatusMission { get; set; }
    public string? SUBITEM { get; set; } = string.Empty;

    #endregion Public Properties
}

#endregion Public Classes