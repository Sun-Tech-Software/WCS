using static Wcs.Common.Enums;

namespace Wcs.Connection.Interface;

public class RunAsi(
    Guid itemGuid,
    string? asi,
    string? subitem,
    string? partition,
    int timeOutPeriod,
    int delayToStepDone,
    ResultCodeTypes resultType,
    int errorResultCode) : DataAsi(asi, subitem, partition)
{
    #region Public Properties

    public int DelayToStepDone { get; set; } = delayToStepDone;
    public int ErrorResultCode { get; set; } = errorResultCode;
    public Guid ItemGuid { get; set; } = itemGuid;
    public ResultCodeTypes ResultType { get; set; } = resultType;
    public int TimeOutPeriod { get; set; } = timeOutPeriod;

    #endregion Public Properties
}