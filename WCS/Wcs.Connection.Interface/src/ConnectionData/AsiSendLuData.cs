namespace Wcs.Connection.Interface.ConnectionData;

public class AsiSendLuData
{
    public int RequestId { get; set; }
    public int DataConfirm { get; set; }
    public int DataErrorCode { get; set; }
    public int LuId { get; set; }
    public int LuContainerType { get; set; }
    public int LuContainerSizeHeight { get; set; }
    public int LuContainerSizeLength { get; set; }
    public int LuContainerSizeWidth { get; set; }
    public int LuHeight { get; set; }
    public int LuLength { get; set; }
    public int LuWidth { get; set; }
    public int LuWeight { get; set; }
    public int LuTemp { get; set; }
    public int LuLengthSide1Surplus { get; set; }
    public int LuLengthSide2Surplus { get; set; }
    public int LuWidthSide1Surplus { get; set; }
    public int LuWidthSide2Surplus { get; set; }
    public int LuHeightSurplus { get; set; }
    public int LuSpareDim1 { get; set; }
    public int LuSpareDim2 { get; set; }
    public int LuSpareDim3 { get; set; }
    public int LuSpareDim4 { get; set; }
    public int LuSpareDim5 { get; set; }
    public int LuSpareDim6 { get; set; }
    public int LuSpareDim7 { get; set; }
    public int LuSpareDim8 { get; set; }
    public int LuSpareDim9 { get; set; }
    public int LuSpareDim10 { get; set; }
    public string? LuCode { get; set; } = string.Empty;
}