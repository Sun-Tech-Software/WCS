using Wcs.Engine.Items;
using Wcs.Engine.Routing;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test.Routing;

/// <summary>
/// Il gateway e' il dispositivo da cui la missione deve passare (documentazione di
/// <see cref="PathMission.Gateway"/>): una rotta che non lo tocca non vale, e il vincolo resta
/// finche' la missione non lo raggiunge.
/// </summary>
public sealed class GatewayTest
{
    [Fact]
    public void ARouteThroughTheGatewayWinsOverAShorterOneThatSkipsIt()
    {
        using var plant = new RoutingTestPlant();
        foreach (var n in new[] { "S", "A", "B", "G", "D" }) plant.Device(n);
        plant.Chain("S", "A", "D");          // due passi, senza gateway
        plant.Chain("S", "B", "G", "D");     // tre passi, con il gateway
        var mission = plant.MissionAt("S", "D");
        mission.Gateway = plant["G"].ItemGuid;

        Assert.Equal(["S", "B", "G", "D"], plant.Names(plant.Route(mission)));
    }

    [Fact]
    public void WithoutARouteThroughTheGatewayThereIsNoRoute()
    {
        using var plant = new RoutingTestPlant();
        foreach (var n in new[] { "S", "A", "G", "D" }) plant.Device(n);
        plant.Chain("S", "A", "D");
        plant.Link("G", "D");                // il gateway esiste ma non e' raggiungibile da S
        var mission = plant.MissionAt("S", "D");
        mission.Gateway = plant["G"].ItemGuid;

        Assert.Empty(plant.Route(mission));
    }

    [Fact]
    public void TheGatewayAsMemberOfTheDestinationGroupIsSatisfiedByTheLastHop()
    {
        using var plant = new RoutingTestPlant();
        plant.Device("S"); plant.Device("Group"); plant.Device("Lane1"); plant.Device("Lane2");
        plant["Group"].GroupItemId = 7;
        plant["Lane1"].BelongingGroupId = 7;
        plant["Lane2"].BelongingGroupId = 7;
        plant.Link("S", "Lane1", priority: 1);
        plant.Link("S", "Lane2", priority: 2);
        var mission = plant.MissionAt("S", "Group");
        mission.Gateway = plant["Lane2"].ItemGuid;

        Assert.Equal(["S", "Lane2"], plant.Names(plant.Route(mission)));
    }

    /// <summary>
    /// Un gateway che e' un dispositivo-gruppo si soddisfa arrivando su un suo membro, con la stessa
    /// regola con cui il setter di <see cref="PathMission.PositionItemId"/> lo azzera. Senza questa
    /// meta' della regola nessuna rotta passerebbe mai dal gruppo, e il vincolo sarebbe
    /// insoddisfacibile per costruzione.
    /// </summary>
    [Fact]
    public void AGatewayThatIsAGroupIsSatisfiedByARouteThroughOneOfItsMembers()
    {
        using var plant = new RoutingTestPlant();
        foreach (var n in new[] { "S", "A", "B", "Lane", "D", "Group" }) plant.Device(n);
        plant["Group"].GroupItemId = 7;
        plant["Lane"].BelongingGroupId = 7;
        plant.Chain("S", "A", "D");            // due passi, senza toccare il gruppo
        plant.Chain("S", "B", "Lane", "D");    // tre passi, attraverso un membro del gruppo
        var mission = plant.MissionAt("S", "D");
        mission.Gateway = plant["Group"].ItemGuid;

        Assert.Equal(["S", "B", "Lane", "D"], plant.Names(plant.Route(mission)));
    }

    /// <summary>
    /// Rete di regressione, non una prova che partiva in rosso: passava anche prima che la
    /// candidata fosse costruita per copia. Cadrebbe se il vincolo venisse valutato sulla rotta
    /// parziale invece che su quella completa.
    /// </summary>
    [Fact]
    public void TheGatewayAsDestinationIsSatisfiedByTheLastHop()
    {
        using var plant = new RoutingTestPlant();
        foreach (var n in new[] { "S", "A", "D" }) plant.Device(n);
        plant.Chain("S", "A", "D");
        var mission = plant.MissionAt("S", "D");
        mission.Gateway = plant["D"].ItemGuid;

        Assert.Equal(["S", "A", "D"], plant.Names(plant.Route(mission)));
    }

    /// <summary>
    /// Da un nodo adiacente alla destinazione la rotta diretta non passa dal gateway: la ricerca
    /// non si ferma li', prosegue sulle altre uscite e trova la deviazione che lo attraversa.
    /// </summary>
    [Fact]
    public void ARejectedArrivalDoesNotStopTheSearchFromThatNode()
    {
        using var plant = new RoutingTestPlant();
        foreach (var n in new[] { "S", "A", "P", "G", "D" }) plant.Device(n);
        plant.Chain("S", "A", "D");
        plant.Chain("A", "P", "G", "D");
        var mission = plant.MissionAt("S", "D");
        mission.Gateway = plant["G"].ItemGuid;

        Assert.Equal(["S", "A", "P", "G", "D"], plant.Names(plant.Route(mission)));
    }

    /// <summary>Un gateway uguale alla sorgente e' gia' raggiunto: non vincola la rotta.</summary>
    [Fact]
    public void AGatewayEqualToTheSourceIsAlreadyReached()
    {
        using var plant = new RoutingTestPlant();
        foreach (var n in new[] { "S", "A", "D" }) plant.Device(n);
        plant.Chain("S", "A", "D");
        var mission = plant.MissionAt("S", "D");
        mission.Gateway = plant["S"].ItemGuid;

        Assert.Equal(["S", "A", "D"], plant.Names(plant.Route(mission)));
    }

    [Fact]
    public void AnUnknownGatewayIsIgnored()
    {
        using var plant = new RoutingTestPlant();
        foreach (var n in new[] { "S", "A", "D" }) plant.Device(n);
        plant.Chain("S", "A", "D");
        var mission = plant.MissionAt("S", "D");
        mission.Gateway = Guid.NewGuid();

        Assert.Equal(["S", "A", "D"], plant.Names(plant.Route(mission)));
    }

    [Fact]
    public void TheGatewaySurvivesAPositionChangeUntilItIsReached()
    {
        using var plant = new RoutingTestPlant();
        foreach (var n in new[] { "S", "A", "G", "D" }) plant.Device(n);
        plant.Chain("S", "A", "G", "D");
        var mission = plant.MissionAt("S", "D");
        var gateway = plant["G"].ItemGuid;
        mission.Gateway = gateway;

        mission.PositionItemId = plant["A"].ItemGuid;
        Assert.Equal(gateway, mission.Gateway);

        mission.PositionItemId = gateway;
        Assert.Equal(Guid.Empty, mission.Gateway);
    }

    /// <summary>
    /// La deframmentazione costruisce i suoi due passi a mano, dalla cella di partenza al gateway
    /// e da li' alla destinazione: solo se l'UDC e' ancora nella cella. Con il gateway che ora
    /// sopravvive ai cambi di posizione, «ancora nella cella» si legge dalla posizione.
    /// </summary>
    [Fact]
    public async Task ADefragmentationStillInTheSourceCellBuildsItsTwoStepsThroughTheGateway()
    {
        using var plant = new RoutingTestPlant();
        foreach (var n in new[] { "S", "G", "D" }) plant.Device(n);
        var mission = plant.MissionAt("S", "D");
        mission.SourceItemId = plant["S"].ItemGuid;
        mission.Gateway = plant["G"].ItemGuid;
        mission.MissionType = MissionTypes.DefragmentCell;

        await mission.BuildPathToDestination();

        Assert.Equal(["S", "G", "D"], plant.Names(mission.MissionSteps.OrderBy(s => s.FromNodeId == plant["S"].ItemGuid ? 0 : 1).ToList()));
    }

    [Fact]
    public async Task ADefragmentationThatHasLeftTheSourceCellDoesNotRebuildAPickUpFromIt()
    {
        using var plant = new RoutingTestPlant();
        foreach (var n in new[] { "S", "G", "A", "D" }) plant.Device(n);
        var mission = plant.MissionAt("A", "D");
        mission.SourceItemId = plant["S"].ItemGuid;
        mission.Gateway = plant["G"].ItemGuid;
        mission.MissionType = MissionTypes.DefragmentCell;

        await mission.BuildPathToDestination();

        Assert.DoesNotContain(Collections.MissionSteps.Instance.List, s => s?.FromNodeId == plant["S"].ItemGuid);
    }

    [Fact]
    public void LeavingThePlantClearsTheGateway()
    {
        using var plant = new RoutingTestPlant();
        foreach (var n in new[] { "S", "G", "D" }) plant.Device(n);
        plant.Chain("S", "G", "D");
        var mission = plant.MissionAt("S", "D");
        mission.Gateway = plant["G"].ItemGuid;

        mission.PositionItemId = Guid.Empty;

        Assert.Equal(Guid.Empty, mission.Gateway);
    }

    /// <summary>La rimozione vera: ActiveMissions.Remove svuota la posizione di una missione a bordo di un dispositivo, e con lei il gateway.</summary>
    [Fact]
    public async Task RemovingTheMissionClearsTheGateway()
    {
        using var plant = new RoutingTestPlant();
        foreach (var n in new[] { "S", "G", "D" }) plant.Device(n);
        plant.Chain("S", "G", "D");
        var mission = plant.MissionAt("S", "D");
        mission.Gateway = plant["G"].ItemGuid;

        Assert.True(await Collections.ActiveMissions.Instance.Remove(mission));

        Assert.Equal(Guid.Empty, mission.PositionItemId);
        Assert.Equal(Guid.Empty, mission.Gateway);
    }
}
