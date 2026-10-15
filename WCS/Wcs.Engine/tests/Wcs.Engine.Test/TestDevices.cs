using Wcs.Engine.Items;

namespace Wcs.Engine.Test;

/// <summary>
/// Dispositivi per le prove che hanno uno store: la riga nasce con il dispositivo, prima di
/// qualunque proprieta'.
/// </summary>
/// <remarks>
/// La riga del dispositivo nasceva come effetto collaterale del primo setter, perche' ogni
/// scrittura era un inserimento-o-aggiornamento. Adesso le scritture del dispositivo aggiornano
/// soltanto, e una riga che manca lascia un Warning (coda, voce 42): in esercizio la riga la crea
/// <c>AutomationDevice.Load</c>, qui la crea <see cref="WithRow"/>, prima dei setter, cosi'
/// giunzioni, mappe porte e missioni trovano la riga a cui appoggiarsi e nessun setter scrive su
/// una riga che non c'e'. Senza store non scrive niente, come l'ORM.
/// </remarks>
internal static class TestDevices
{
    public static AutomationDevice WithRow(Guid itemId, Action<AutomationDevice>? configure = null)
    {
        var device = new AutomationDevice(itemId);
        ORM.AutomationDevice.Create(device);
        configure?.Invoke(device);
        return device;
    }
}
