using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test.Routing;

/// <summary>
/// Le sonde: chi deve sapere il prossimo salto legge la rotta pianificata, chi deve provare una
/// destinazione ipotetica non scrive sulla missione.
/// </summary>
public sealed class RouteProbeTest
{
    [Fact]
    public void ThePickUpCheckLooksAtThePlannedNextDeviceNotAtARecomputedRoute()
    {
        using var plant = new RoutingTestPlant();
        foreach (var n in new[] { "A", "B", "C", "D" }) plant.Device(n);
        plant.Chain("A", "B", "C");
        plant.Link("B", "D");
        // La rotta pianificata va per D; una ricerca di oggi andrebbe per C (C libero, D fermo)
        var mission = plant.MissionAt("A", "C");
        plant.PlannedStep(mission, "A", "B");
        plant.PlannedStep(mission, "B", "D");
        plant["D"].Status = DeviceStatusTypes.Error;

        var canPickUp = plant["B"].LuManager!.CanPickUpLu(mission);

        Assert.False(canPickUp);
        Assert.Equal("The destination item is not ready to accept LU", mission.MovingDescription);
    }

    /// <summary>
    /// Una missione ancora in coda di prelievo non ha step pianificati: il controllo torna alla
    /// sonda, e il prelievo si rifiuta se il salto successivo non e' pronto, come prima.
    /// </summary>
    [Fact]
    public void WithoutAPlannedStepThePickUpCheckFallsBackToTheProbe()
    {
        using var plant = new RoutingTestPlant();
        foreach (var n in new[] { "A", "B", "C" }) plant.Device(n);
        plant.Chain("A", "B", "C");
        var mission = plant.MissionAt("A", "C");
        plant["C"].Status = DeviceStatusTypes.Error;

        var canPickUp = plant["B"].LuManager!.CanPickUpLu(mission);

        Assert.False(canPickUp);
        Assert.Equal("The destination item is not ready to accept LU", mission.MovingDescription);
    }

    [Fact]
    public void TheProbeTowardsAHypotheticalDestinationLeavesTheMissionUntouched()
    {
        using var plant = new RoutingTestPlant();
        foreach (var n in new[] { "A", "B", "C" }) plant.Device(n);
        plant.Chain("A", "B", "C");
        var mission = plant.MissionAt("A", "C");
        var step = plant.PlannedStep(mission, "A", "B");
        var destinationBefore = mission.DestinationItemId;

        var probe = mission.ProbeRouteSteps(plant["B"].ItemGuid);

        Assert.Equal(["A", "B"], plant.Names(probe));
        Assert.Equal(destinationBefore, mission.DestinationItemId);
        Assert.Contains(step, MissionSteps.Instance.List);
    }

    [Fact]
    public void TheProbeTriesTheOffLinePassLikeTheRealSearch()
    {
        using var plant = new RoutingTestPlant();
        foreach (var n in new[] { "A", "B", "C" }) plant.Device(n);
        plant.Chain("A", "B", "C");
        plant["B"].Status = DeviceStatusTypes.OffLine;
        var mission = plant.MissionAt("A", "C");

        Assert.Equal(["A", "B", "C"], plant.Names(mission.ProbeRouteSteps(plant["C"].ItemGuid)));
    }
}
