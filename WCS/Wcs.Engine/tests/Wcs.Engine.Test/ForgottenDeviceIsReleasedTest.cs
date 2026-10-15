using System.Reflection;
using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.Test.Routing;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Un dispositivo tolto dall'impianto (<c>DELETE /plant/items</c>, o il salvataggio del disegno
/// che non lo contiene piu') va rilasciato, come fa gia' <c>PathDevices.Clear</c>.
/// </summary>
/// <remarks>
/// <c>PathDevices.Remove(Guid)</c> lo toglieva dalla collezione e dallo store e basta: restava
/// iscritto all'evento statico <c>Navigator.StatusChanged</c> con i propri cicli di sfondo, e al
/// passaggio successivo a <c>Run</c> riceveva <c>ExecuteStep</c> come se fosse ancora
/// nell'impianto - con la sua ASI, la sua connessione e il suo carico: un fantasma che comanda lo
/// stesso PLC. E' la stessa strada con cui nascono i dispositivi zombie dei collaudi (D144).
/// </remarks>
public sealed class ForgottenDeviceIsReleasedTest : IDisposable
{
    private readonly RoutingTestPlant _plant = new(NavigatorStatusTypes.Stop);

    public void Dispose()
    {
        _plant.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ADeviceRemovedFromThePlantIsNoLongerSubscribedToTheNavigatorStatus()
    {
        var device = _plant.Device("X");
        Assert.True(IsSubscribed(device), "il dispositivo appena costruito deve essere iscritto: altrimenti la prova non discrimina");

        Assert.True(PathDevices.Instance.Remove(device.ItemGuid));

        Assert.False(IsSubscribed(device));
        Assert.Null(PathDevices.Instance[device.ItemGuid]);
    }

    /// <summary>La meta' che deve restare vera: un dispositivo che resta nell'impianto resta iscritto.</summary>
    [Fact]
    public void ADeviceThatStaysInThePlantStaysSubscribed()
    {
        var stays = _plant.Device("Y");
        var goes = _plant.Device("Z");

        PathDevices.Instance.Remove(goes.ItemGuid);

        Assert.True(IsSubscribed(stays));
    }

    /// <summary>L'elenco degli iscritti all'evento statico, letto dal campo generato dal compilatore.</summary>
    private static bool IsSubscribed(AutomationDevice device)
    {
        var field = typeof(Navigator).GetField("StatusChanged", BindingFlags.Static | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("il campo dell'evento StatusChanged non si trova: il nome e' cambiato");
        return field.GetValue(null) is Delegate handlers &&
               handlers.GetInvocationList().Any(h => ReferenceEquals(h.Target, device));
    }
}
