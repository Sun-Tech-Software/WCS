using Wcs.Connection.Interface.ConnectionData;

namespace Wcs.Connection.Interface.Events;

public class AsiRequestsLuInfoEventArgs(AsiRequestsLuInfoData valueLuDataFromAsiResult) : EventArgs
{
    public AsiRequestsLuInfoData LuDataFromAsiResult = valueLuDataFromAsiResult;
}