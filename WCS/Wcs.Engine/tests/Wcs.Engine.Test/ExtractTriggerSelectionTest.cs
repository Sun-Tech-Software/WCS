using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.Test.Routing;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Which storing missions a device feeding a storage receives first
/// (<see cref="AutomationDevice.StoringByExtractTrigger"/>): those from the group of a loaded
/// extraction trigger point, the first point before the second. The second point used to be checked
/// against the group of the first one, so with only the second point loaded every storing mission
/// was taken, from any group.
/// </summary>
public sealed class ExtractTriggerSelectionTest : IDisposable
{
    private readonly RoutingTestPlant _plant = new();

    public void Dispose() => _plant.Dispose();

    [Fact]
    public void WithOnlyTheSecondPointLoadedItsGroupIsReceivedFirst()
    {
        var (point1, point2) = TwoTriggerGroups();
        var fromPoint2 = StoringAt("B1");
        var elsewhere = StoringAt("C");

        var chosen = AutomationDevice.StoringByExtractTrigger([elsewhere, fromPoint2], point1, point2);

        Assert.Equal([fromPoint2], chosen);
    }

    [Fact]
    public void TheFirstPointWinsWhenBothHaveMissionsToReceive()
    {
        var (point1, point2) = TwoTriggerGroups();
        var fromPoint1 = StoringAt("A1");
        var fromPoint2 = StoringAt("B1");

        var chosen = AutomationDevice.StoringByExtractTrigger([fromPoint2, fromPoint1], point1, point2);

        Assert.Equal([fromPoint1], chosen);
    }

    [Fact]
    public void ALoadedFirstPointWithNothingToSendLeavesTheChoiceToTheSecond()
    {
        var (point1, point2) = TwoTriggerGroups();
        _plant.MissionAt("A1", null);
        var fromPoint2 = StoringAt("B1");
        var elsewhere = StoringAt("C");

        var chosen = AutomationDevice.StoringByExtractTrigger([elsewhere, fromPoint2], point1, point2);

        Assert.Equal([fromPoint2], chosen);
    }

    [Fact]
    public void WithNoLoadedPointEveryStoringMissionIsReceived()
    {
        var (point1, point2) = TwoTriggerGroups();
        var elsewhere = StoringAt("C");
        var notStoring = _plant.MissionAt("D", null);
        notStoring.MissionType = MissionTypes.Extraction;

        var chosen = AutomationDevice.StoringByExtractTrigger([elsewhere, notStoring], point1, point2);

        Assert.Equal([elsewhere], chosen);
    }

    private (AutomationDevice Point1, AutomationDevice Point2) TwoTriggerGroups()
    {
        var point1 = Group("G1", 1, "A1");
        var point2 = Group("G2", 2, "B1");
        _plant.Device("C");
        _plant.Device("D");
        return (point1, point2);
    }

    private AutomationDevice Group(string name, int groupId, string member)
    {
        var group = _plant.Device(name, DeviceTypes.GroupOfAutomationDevices);
        group.GroupItemId = groupId;
        _plant.Device(member).BelongingGroupId = groupId;
        return group;
    }

    private PathMission StoringAt(string position)
    {
        var mission = _plant.MissionAt(position, null);
        mission.MissionType = MissionTypes.Storing;
        Assert.Same(mission, PathDevices.Instance[mission.PositionItemId]!.LoadedLu);
        return mission;
    }
}
