using Wcs.Connection.Interface.ConnectionData;
using static Wcs.Common.Enums;

namespace Wcs.Connection.Interface.Events;

public class AsiSendLuDataEventArgs(
    Guid valueItemGuid,
    ResultCodeTypes valueResult,
    AsiSendLuData? valueLuDataFromAsiResult) : EventArgs
{
    public Guid ItemGuid = valueItemGuid;
    public AsiSendLuData? LuDataFromAsiResult = valueLuDataFromAsiResult;
    public ResultCodeTypes Result = valueResult;
}