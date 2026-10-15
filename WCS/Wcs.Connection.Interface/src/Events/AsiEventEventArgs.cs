using Wcs.Connection.Interface.ConnectionData;

namespace Wcs.Connection.Interface.Events;

public class AsiEventEventArgs(AsiEventData? value) : EventArgs
{
    public AsiEventData? Data = value;
}