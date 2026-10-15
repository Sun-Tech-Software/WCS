using Wcs.Connection.Interface.ConnectionData;
using static Wcs.Common.Enums;

namespace Wcs.Connection.Interface.Events;

public class AsiMovementIsCompletedEventArgs(Guid valueItemGuid, Guid valueStepGuid, ResultCodeTypes valueResult, int valueErrorCode, string valueErrorDescription, AsiMovementIsCompletedData? ValueMoveLuResult) : EventArgs
{
    public Guid ItemGuid = valueItemGuid;
    public Guid StepGuid = valueStepGuid;
    public ResultCodeTypes Result = valueResult;
    public int ErrorCode = valueErrorCode;
    public AsiMovementIsCompletedData? MoveLuResult = ValueMoveLuResult;
    public string ErrorDescription = valueErrorDescription;
}
