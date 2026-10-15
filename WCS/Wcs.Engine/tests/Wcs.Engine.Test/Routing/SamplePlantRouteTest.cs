using System.Text.Json;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test.Routing;

/// <summary>
/// An invented sample plant as fixture (<c>Fixtures/sample-plant.json</c>, 28 devices and 33
/// junctions): two infeed bays, a circular distributor, three lifts on route level 5 (to storage A,
/// to storage B, to shipping), two storages each served by its shuttle on route level 1, a transfer
/// from shuttle A to shuttle B, a return lane from each shuttle, three outbound bays (buffers) and a
/// recirculation loop from shipping back to the distributor. It describes no real installation.
/// All devices are <c>Ready</c>, no step is queued, there is no gateway.
/// <para>
/// The expected values were derived by hand from the drawing and cross-checked with an independent
/// model of the rules (shortest simple path in steps; storages and buffers are never crossed; at most
/// one node per non-zero level, the source included and the destination excluded), not by running
/// the search under test. Every case below depends on one of those rules: without the level rule
/// the route of <see cref="FromTheStorageToTheBufferTheRouteGoesThroughTheReturnLaneNotThroughTheOtherShuttle"/>
/// would cross the other shuttle (one step shorter), and the unreachable pairs would be reachable.
/// </para>
/// </summary>
public sealed class SamplePlantRouteTest
{
    private sealed record FixtureItem(string Name, int DeviceType, int Level);
    private sealed record FixtureJunction(string From, string To, int Priority);
    private sealed record Fixture(string Source, List<FixtureItem> Items, List<FixtureJunction> Junctions);

    private static RoutingTestPlant LoadPlant()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Routing", "Fixtures", "sample-plant.json");
        var fixture = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        var plant = new RoutingTestPlant();
        foreach (var item in fixture.Items)
            plant.Device(item.Name, (DeviceTypes)item.DeviceType, item.Level);
        foreach (var junction in fixture.Junctions)
            plant.Link(junction.From, junction.To, junction.Priority);
        return plant;
    }

    [Theory]
    [InlineData("IN01", "ST01", 8)]    // infeed, distributor, lift A, shuttle A, storage A
    [InlineData("ST01", "OUT01", 10)]  // out of storage A through return lane A and the shipping lift
    [InlineData("ST02", "OUT02", 9)]   // out of storage B through the short return lane B
    [InlineData("CV06", "ST01", 8)]    // after the shipping lift, back through the recirculation loop
    public void KnownPairsHaveTheExpectedLength(string from, string to, int steps)
    {
        using var plant = LoadPlant();

        Assert.Equal(steps, plant.Route(plant.MissionAt(from, to)).Count);
    }

    [Fact]
    public void FromTheStorageToTheBufferTheRouteGoesThroughTheReturnLaneNotThroughTheOtherShuttle()
    {
        using var plant = LoadPlant();

        var names = plant.Names(plant.Route(plant.MissionAt("ST01", "OUT01")));

        Assert.Contains("CV11", names);
        Assert.Contains("CV13", names);
        Assert.DoesNotContain("SH02", names);
    }

    [Theory]
    [InlineData("ST01", "ST02")]   // the two shuttles are both on level 1
    [InlineData("ST02", "ST01")]
    [InlineData("CV05", "ST01")]   // from CV05 on, storage A would need two level-5 lifts
    [InlineData("IN01", "IN02")]   // two infeed bays, no junction between them
    public void KnownPairsAreUnreachable(string from, string to)
    {
        using var plant = LoadPlant();

        Assert.Empty(plant.Route(plant.MissionAt(from, to)));
    }

    /// <summary>
    /// The count over all ordered pairs is the widest regression guard: any change to the search
    /// that alters a single route changes this number or the longest route.
    /// </summary>
    [Fact]
    public void AllOrderedPairsReachableCountAndLongestRouteMatchTheModel()
    {
        using var plant = LoadPlant();
        var names = plant.DeviceNames.ToList();

        var reachable = 0;
        var longest = 0;
        foreach (var from in names)
        foreach (var to in names)
        {
            if (from == to) continue;
            var mission = plant.MissionAt(from, to);
            var steps = plant.Route(mission);
            Collections.ActiveMissions.Instance.RemoveAll(m => m.MissionId == mission.MissionId);
            if (steps.Count == 0) continue;
            reachable++;
            longest = Math.Max(longest, steps.Count);
        }

        Assert.Equal(471, reachable);
        Assert.Equal(11, longest);
    }
}
