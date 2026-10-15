using Wcs.Connection.Interface.Events;

namespace Wcs.Connection.Interface;

public class Handlers
{
    public delegate void AsiEventEventHandler(object sender, AsiEventEventArgs e);

    public delegate void AsiMovementIsCompletedEventHandler(object sender, AsiMovementIsCompletedEventArgs e);

    public delegate void AsiRequestsLuInfoEventHandler(object sender, AsiRequestsLuInfoEventArgs e);

    public delegate void AsiSendLuDataEventHandler(object sender, AsiSendLuDataEventArgs e);

    public delegate void LuUnloadedFromAsiEventHandler(object sender, LuUnloadedFromAsiEventArgs e);

    public delegate void NewLuOnAsiEventHandler(object sender, NewLuOnAsiEventArgs e);
}