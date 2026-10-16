using Wcs.Engine.Test.Routing;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// <see cref="Items.AutomationDevice.LUsToReceive"/> looks at every source: a loaded source whose
/// load unit has no route yet (no step to do, as while it waits for its cell) is skipped, it does not
/// end the search. It used to return there, and the sources after it were never looked at.
/// </summary>
public sealed class LUsToReceiveTest : IDisposable
{
    private readonly RoutingTestPlant _plant = new();

    public void Dispose() => _plant.Dispose();

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void ASourceWithoutARouteDoesNotHideTheNextOne(int groupItemId)
    {
        var receiver = _plant.Device("X");
        receiver.GroupItemId = groupItemId;
        _plant.Device("S1").PriorityInRoutePath = 1;
        _plant.Device("S2").PriorityInRoutePath = 2;
        _plant.Link("S1", "X");
        _plant.Link("S2", "X");
        _plant.MissionAt("S1", null);
        var coming = _plant.MissionAt("S2", "X");
        _plant.PlannedStep(coming, "S2", "X");

        // GroupItemId 0 takes the first branch of LUsToReceive, any other value the second.
        Assert.Equal([coming], receiver.LUsToReceive);
    }
}
