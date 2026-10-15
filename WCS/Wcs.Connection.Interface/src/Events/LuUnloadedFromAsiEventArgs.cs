using Wcs.Connection.Interface.ConnectionData;

namespace Wcs.Connection.Interface.Events;

public class LuUnloadedFromAsiEventArgs(LuUnloadedFromAsiData? value) : EventArgs
{
    public LuUnloadedFromAsiData? Data = value;
}