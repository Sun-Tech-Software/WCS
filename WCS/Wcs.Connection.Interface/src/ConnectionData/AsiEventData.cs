namespace Wcs.Connection.Interface.ConnectionData;

public class AsiEventData
{

    #region Public Properties

    public string Asi { get; set; } = string.Empty;
    public int EventCode { get; set; }

    public int EventParamNo => EventParams.Count;

    public List<string> EventParams { get; set; } = [];
    public string Partition { get; set; } = string.Empty;
    public string Subitem { get; set; } = string.Empty;

    #endregion Public Properties
}