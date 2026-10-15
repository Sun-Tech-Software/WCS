using Wcs.Engine.Collections;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test.Routing;

/// <summary>
/// Le regole della ricerca dei percorsi come sono oggi (rapporto del 2026-09-04, sezioni 1.3-1.5):
/// scritte sul codice attuale, sono la rete per gli interventi successivi. Un test che qui cade
/// dice che una regola e' diversa da come e' descritta: si indaga, non si adatta il test.
/// </summary>
public sealed class RouteRulesTest
{
    [Fact]
    public void AChainGivesOneStepPerHopWithMissionAndDate()
    {
        using var plant = new RoutingTestPlant();
        plant.Device("A"); plant.Device("B"); plant.Device("C");
        plant.Chain("A", "B", "C");
        var mission = plant.MissionAt("A", "C");

        var steps = plant.Route(mission);

        Assert.Equal(["A", "B", "C"], plant.Names(steps));
        Assert.All(steps, s => Assert.Equal(mission.MissionId, s.MissionId));
        Assert.All(steps, s => Assert.NotEqual(default, s.Date));
        Assert.All(steps, s => Assert.Equal(0, s.FromGroupRoutePath));
    }

    [Fact]
    public void TheShorterRouteWinsEvenIfTheLongerOneIsDiscoveredFirst()
    {
        using var plant = new RoutingTestPlant();
        foreach (var n in new[] { "S", "L1", "L2", "L3", "K", "D" }) plant.Device(n);
        // La lunga ha priorita' 1 (esplorata prima), la corta 99
        plant.Link("S", "L1", priority: 1); plant.Chain("L1", "L2", "L3", "D");
        plant.Link("S", "K", priority: 99); plant.Link("K", "D");

        var steps = plant.Route(plant.MissionAt("S", "D"));

        Assert.Equal(["S", "K", "D"], plant.Names(steps));
    }

    [Fact]
    public void AmongEqualLengthRoutesTheOneWithFewerQueuedStepsWins()
    {
        using var plant = new RoutingTestPlant();
        foreach (var n in new[] { "S", "P", "Q", "D", "X" }) plant.Device(n);
        plant.Link("S", "P", priority: 1); plant.Link("P", "D");
        plant.Link("S", "Q", priority: 2); plant.Link("Q", "D");
        // Un'altra missione ha uno step pianificato che parte da P: P «costa» uno step in coda
        var other = plant.MissionAt("X", "D");
        plant.PlannedStep(other, "P", "D");

        var steps = plant.Route(plant.MissionAt("S", "D"));

        Assert.Equal(["S", "Q", "D"], plant.Names(steps));
    }

    [Fact]
    public void AnOffLineDeviceIsAvoidedWhenThereIsAnAlternativeAndCrossedOnlyInTheSecondPass()
    {
        using var plant = new RoutingTestPlant();
        foreach (var n in new[] { "S", "O", "D", "A1", "A2" }) plant.Device(n);
        plant.Chain("S", "O", "D");
        plant.Chain("S", "A1", "A2", "D");
        plant["O"].Status = DeviceStatusTypes.OffLine;
        Assert.Equal(DeviceStatusTypes.OffLine, plant["O"].Status);

        var mission = plant.MissionAt("S", "D");

        Assert.Equal(["S", "A1", "A2", "D"], plant.Names(new PathBuilder(mission).GetRouteSteps() ?? []));

        // Senza alternativa: la prima passata e' vuota, la seconda passa sul fuori linea
        PathJunctions.Instance.RemoveAll(j => j.SourceId == plant["S"].ItemGuid && j.DestinationId == plant["A1"].ItemGuid);
        Assert.Empty(new PathBuilder(mission).GetRouteSteps() ?? []);
        Assert.Equal(["S", "O", "D"], plant.Names(new PathBuilder(mission).GetRouteSteps(true) ?? []));
    }

    [Theory]
    [InlineData(DeviceStatusTypes.Error)]
    [InlineData(DeviceStatusTypes.Warning)]
    public void AnErrorDeviceIsCrossedOnlyInTheSecondPass(DeviceStatusTypes status)
    {
        using var plant = new RoutingTestPlant();
        foreach (var n in new[] { "S", "E", "D" }) plant.Device(n);
        plant.Chain("S", "E", "D");
        plant["E"].Status = status;
        Assert.Equal(status, plant["E"].Status);
        var mission = plant.MissionAt("S", "D");

        Assert.Empty(new PathBuilder(mission).GetRouteSteps() ?? []);
        Assert.Equal(["S", "E", "D"], plant.Names(new PathBuilder(mission).GetRouteSteps(true) ?? []));
    }

    [Fact]
    public void AMovingDeviceWithALoadUnitBoundElsewhereIsAvoidedFirstAndAcceptedWithoutAlternative()
    {
        using var plant = new RoutingTestPlant();
        foreach (var n in new[] { "S", "M", "D", "A1", "A2", "Z" }) plant.Device(n);
        plant.Chain("S", "M", "D");
        plant.Chain("S", "A1", "A2", "D");
        plant.Link("M", "Z");
        // M sta muovendo un'UDC diretta a Z, con uno step in coda
        var other = plant.MissionAt("M", "Z");
        plant.PlannedStep(other, "M", "Z");
        plant["M"].Status = DeviceStatusTypes.Moving;

        var mission = plant.MissionAt("S", "D");
        Assert.Equal(["S", "A1", "A2", "D"], plant.Names(plant.Route(mission)));

        // Senza alternativa la passata Normal accetta di accodarsi
        PathJunctions.Instance.RemoveAll(j => j.SourceId == plant["S"].ItemGuid && j.DestinationId == plant["A1"].ItemGuid);
        Assert.Equal(["S", "M", "D"], plant.Names(new PathBuilder(mission).GetRouteSteps() ?? []));
    }

    [Fact]
    public void AMovingDeviceWithALoadUnitBoundToTheSameDestinationIsNotBlocking()
    {
        using var plant = new RoutingTestPlant();
        foreach (var n in new[] { "S", "M", "D", "A1", "A2" }) plant.Device(n);
        plant.Chain("S", "M", "D");
        plant.Chain("S", "A1", "A2", "D");
        var other = plant.MissionAt("M", "D");
        plant.PlannedStep(other, "M", "D");
        plant["M"].Status = DeviceStatusTypes.Moving;

        Assert.Equal(["S", "M", "D"], plant.Names(plant.Route(plant.MissionAt("S", "D"))));
    }

    /// <summary>
    /// La fermata su magazzini e buffer non e' assoluta: <c>RouteSearch.Visit</c> cerca l'arrivo
    /// (<c>RouteSearch.cs:83</c>) <b>prima</b> di controllare la fermata (<c>RouteSearch.cs:97</c>),
    /// quindi una destinazione adiacente al buffer viene raggiunta attraversandolo. Oltre quel
    /// passo la ricerca si ferma davvero.
    /// </summary>
    [Theory]
    [InlineData(DeviceTypes.Buffer)]
    [InlineData(DeviceTypes.Storage)]
    public void BuffersAndStoragesStopTheSearchButAnAdjacentDestinationIsStillReached(DeviceTypes type)
    {
        using var plant = new RoutingTestPlant();
        plant.Device("S"); plant.Device("B", type); plant.Device("N"); plant.Device("D"); plant.Device("T", type);
        plant.Chain("S", "B", "N", "D");
        plant.Link("T", "S");

        Assert.Empty(plant.Route(plant.MissionAt("S", "D")));                            // due passi oltre il buffer non si va
        Assert.Equal(["S", "B", "N"], plant.Names(plant.Route(plant.MissionAt("S", "N")))); // il vicino del buffer si raggiunge
        Assert.Equal(["S", "B"], plant.Names(plant.Route(plant.MissionAt("S", "B"))));   // arrivo sul buffer
        Assert.Equal(["T", "S", "B"], plant.Names(plant.Route(plant.MissionAt("T", "B")))); // partenza da un magazzino
    }

    [Fact]
    public void ALoadingAndBufferDeviceCanBeCrossed()
    {
        using var plant = new RoutingTestPlant();
        plant.Device("S"); plant.Device("LB", DeviceTypes.LoadingAndBuffer); plant.Device("D");
        plant.Chain("S", "LB", "D");

        Assert.Equal(["S", "LB", "D"], plant.Names(plant.Route(plant.MissionAt("S", "D"))));
    }

    [Fact]
    public void AtMostOneNodePerGroupLevelAndTheDestinationIsExempt()
    {
        using var plant = new RoutingTestPlant();
        plant.Device("S"); plant.Device("G1", level: 5); plant.Device("M"); plant.Device("G2", level: 5); plant.Device("D");
        plant.Device("A1"); plant.Device("A2"); plant.Device("A3"); plant.Device("A4");
        plant.Chain("S", "G1", "M", "G2", "D");                 // corta, ma con due nodi di livello 5
        plant.Chain("S", "A1", "A2", "A3", "A4", "D");          // lunga, ammessa

        Assert.Equal(["S", "A1", "A2", "A3", "A4", "D"], plant.Names(plant.Route(plant.MissionAt("S", "D"))));

        // La destinazione non passa dal controllo di livello
        Assert.Equal(["S", "G1", "M", "G2"], plant.Names(plant.Route(plant.MissionAt("S", "G2"))));
    }

    [Fact]
    public void TwoAdjacentNodesOfTheSameLevelAreRefused()
    {
        using var plant = new RoutingTestPlant();
        plant.Device("S"); plant.Device("G1", level: 5); plant.Device("G2", level: 5); plant.Device("D");
        plant.Chain("S", "G1", "G2", "D");

        Assert.Empty(plant.Route(plant.MissionAt("S", "D")));
    }

    [Fact]
    public void ARingTerminatesAndDoesNotReturnToTheSource()
    {
        using var plant = new RoutingTestPlant();
        foreach (var n in new[] { "A", "B", "C", "D" }) plant.Device(n);
        plant.Chain("A", "B", "C", "A");
        plant.Link("C", "D");

        Assert.Equal(["A", "B", "C", "D"], plant.Names(plant.Route(plant.MissionAt("A", "D"))));
    }

    [Fact]
    public void AnUnreachableDestinationGivesAnEmptyRoute()
    {
        using var plant = new RoutingTestPlant();
        foreach (var n in new[] { "A", "B", "C" }) plant.Device(n);
        plant.Chain("A", "B");

        Assert.Empty(plant.Route(plant.MissionAt("A", "C")));
    }

    [Fact]
    public void SourceEqualToDestinationGivesAnEmptyRouteWithoutSearching()
    {
        using var plant = new RoutingTestPlant();
        plant.Device("A"); plant.Device("B");
        plant.Chain("A", "B", "A");

        Assert.Empty(plant.Route(plant.MissionAt("A", "A")));
    }

    [Fact]
    public void AReplanStartsFromThePositionNotFromTheSource()
    {
        using var plant = new RoutingTestPlant();
        foreach (var n in new[] { "A", "B", "C", "D" }) plant.Device(n);
        plant.Chain("A", "B", "C", "D");
        var mission = plant.MissionAt("C", "D");
        mission.SourceItemId = plant["A"].ItemGuid;

        Assert.Equal(["C", "D"], plant.Names(plant.Route(mission)));
    }

    [Fact]
    public void ADestinationGroupIsReachedThroughAnyMember()
    {
        using var plant = new RoutingTestPlant();
        plant.Device("S"); plant.Device("Group"); plant.Device("Member");
        plant["Group"].GroupItemId = 7;
        plant["Member"].BelongingGroupId = 7;
        plant.Chain("S", "Member");

        Assert.Equal(["S", "Member"], plant.Names(plant.Route(plant.MissionAt("S", "Group"))));
    }
}
