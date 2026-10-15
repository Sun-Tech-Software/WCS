using static Wcs.Common.Enums;

namespace Wcs.Common.Events;

public class AutomationDeviceStatusChangedEventArgs(LuLoadStatusTypes valueLu, DeviceStatusTypes valueItem) : EventArgs
{

    #region Public Fields

    public DeviceStatusTypes ItemStatus = valueItem;
    public LuLoadStatusTypes LuStatus = valueLu;

    #endregion Public Fields

}
