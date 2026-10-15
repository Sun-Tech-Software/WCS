using Log.Interface;
using Wcs.Engine.Test.Routing;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Voce D90: una missione con <c>ActiveFilter</c> diretta al magazzino toglie dalla ricerca dei
/// percorsi i dispositivi <c>ToBeFiltered</c> dei suoi step, e li toglie a tutte le estrazioni.
/// Finche' quella missione non si chiude — un dispositivo fuori linea per ore — nessuna
/// estrazione trova rotta, e dal registro non si capiva perche': nessuna riga diceva quale
/// missione stesse filtrando che cosa. Ora <c>PathBuilder</c> lo scrive, come avviso, con
/// l'eta' della missione filtrante e i dispositivi che tiene fuori.
/// </summary>
/// <remarks>
/// L'impianto si prepara in <c>Recover</c>, dove i setter della missione non hanno effetti
/// collaterali, e passa a <c>Stop</c> solo per la ricerca: in <c>Recover</c> il filtro non si
/// inizializza affatto (<c>ShouldSkipFilterInitialization</c>). Su DEMOPLANT il meccanismo e'
/// spento — nessun dispositivo ha <c>ToBeFiltered</c> — quindi questo e' l'unico posto in cui
/// oggi lo si vede acceso.
/// </remarks>
public sealed class FilteringMissionIsLoggedTest : IDisposable
{
    private readonly RoutingTestPlant _plant = new();

    public void Dispose()
    {
        _plant.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task TheSearchSaysWhichMissionFiltersWhichDevices()
    {
        using var log = new RecordingLogWriter();

        var warehouse = _plant.Device("W", DeviceTypes.Storage);
        var relocator = _plant.Device("R");
        var gate = _plant.Device("P");
        _plant.Device("X");
        _plant.Chain("R", "P", "W");
        _plant.Link("P", "X");

        relocator.ToBeFiltered = true;

        // Il deposito in arrivo che protegge il tratto: filtra, va al magazzino, passa dal
        // ricollocatore.
        var storing = _plant.MissionAt("R", "W");
        storing.ActiveFilter = true;
        _plant.PlannedStep(storing, "R", "P");
        _plant.PlannedStep(storing, "P", "W");

        // L'estrazione che ne subisce il filtro: parte da un dispositivo collegato al magazzino.
        var extraction = _plant.MissionAt("P", "X");

        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        _ = new PathBuilder(extraction);

        await log.Wait((description, level) =>
                level == LogEntryType.Warning &&
                description.Contains(storing.MissionId.ToString()) &&
                description.Contains(relocator.ItemGuid.ToString()))
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(warehouse.ItemGuid, storing.DestinationItemId);
    }
}
