using Wcs.Connection.Interface.ConnectionData;

namespace Wcs.Connection.Interface.Events;

public class NewLuOnAsiEventArgs(NewLuOnAsiData value) : EventArgs
{
    public NewLuOnAsiData Data = value;
}