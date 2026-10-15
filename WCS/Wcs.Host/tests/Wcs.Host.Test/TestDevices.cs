using Wcs.Engine.Items;

namespace Wcs.Host.Test;

/// <summary>
/// Dispositivi per le prove con lo store dell'host: la riga nasce con il dispositivo, prima di
/// qualunque proprieta'. Gemello di <c>Wcs.Engine.Test.TestDevices</c>.
/// </summary>
/// <remarks>
/// Le scritture del dispositivo aggiornano soltanto (coda, voce 42): la riga non nasce piu' dal
/// primo setter. In esercizio la crea <c>AutomationDevice.Load</c>; qui <see cref="WithRow"/>, cosi'
/// missioni e giunzioni trovano la riga a cui appoggiarsi.
/// </remarks>
internal static class TestDevices
{
    public static AutomationDevice WithRow(Guid itemId, Action<AutomationDevice>? configure = null)
    {
        var device = new AutomationDevice(itemId);
        global::Wcs.Engine.ORM.AutomationDevice.Create(device);
        configure?.Invoke(device);
        return device;
    }
}
