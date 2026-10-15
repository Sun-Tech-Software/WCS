using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Live;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

public sealed class LiveRouteTest
{
    private static readonly Guid Here = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid Next = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");
    private static readonly Guid Warehouse = Guid.Parse("cccccccc-0000-4000-8000-000000000003");
    private static readonly Guid Relocator = Guid.Parse("dddddddd-0000-4000-8000-000000000004");
    private static readonly Guid Mission = Guid.Parse("eeeeeeee-0000-4000-8000-000000000005");

    private static PlantItemDto Item(Guid id, DeviceTypes type, Guid? relocate = null) =>
        new(id, id.ToString()[..4], type, null, null, null, default, default, 0, relocate,
            0, 0, 0, 0, default!);

    private static MissionDto MissionTo(Guid? destination) =>
        new(Mission, 7m, "LU-7", Here, null, destination, DateTime.UtcNow, null, null, null, default, null, null,
            default, null, default, 0m, 0m, 0m, null, 0, null, null, null, null, null, null, null, 0m, null, 0m,
            false, 0m, false, false);

    private static MissionStepDto StepFrom(Guid from, Guid to, bool ended) =>
        new(Guid.NewGuid(), Mission, from, to, null, DateTime.UtcNow, DateTime.UtcNow,
            ended ? DateTime.UtcNow : default, default, Guid.Empty, 0);

    private static Func<Guid, PlantItemDto?> Plant(params PlantItemDto[] items) =>
        id => items.FirstOrDefault(item => item.ItemId == id);

    [Fact]
    public void TheNextStepIsTheFirstOpenOneLeavingThisDevice()
    {
        var route = LiveRoute.Of(Here, MissionTo(null),
            [StepFrom(Here, Warehouse, ended: true), StepFrom(Next, Warehouse, ended: false), StepFrom(Here, Next, ended: false)],
            Plant());

        Assert.Equal(Next, route.Next);
    }

    [Fact]
    public void NoNextStepWhenNoneLeavesFromHere()
    {
        var route = LiveRoute.Of(Here, MissionTo(null), [StepFrom(Next, Warehouse, ended: false)], Plant());

        Assert.Null(route.Next);
    }

    [Fact]
    public void TheFinalDestinationIsTheMissionsDestination()
    {
        var route = LiveRoute.Of(Here, MissionTo(Next), [], Plant(Item(Next, DeviceTypes.Automation)));

        Assert.Equal(Next, route.Final);
    }

    [Fact]
    public void AWarehouseDestinationPointsAtItsRelocator()
    {
        var route = LiveRoute.Of(Here, MissionTo(Warehouse), [],
            Plant(Item(Warehouse, DeviceTypes.Storage, Relocator)));

        Assert.Equal(Relocator, route.Final);
    }

    [Fact]
    public void AWarehouseWithoutARelocatorIsItselfTheDestination()
    {
        var route = LiveRoute.Of(Here, MissionTo(Warehouse), [], Plant(Item(Warehouse, DeviceTypes.Storage, Guid.Empty)));

        Assert.Equal(Warehouse, route.Final);
    }

    [Fact]
    public void ADestinationTheEditorDoesNotKnowIsStillTheDestination()
    {
        var route = LiveRoute.Of(Here, MissionTo(Next), [], Plant());

        Assert.Equal(Next, route.Final);
    }

    [Fact]
    public void NoFinalArrowWhenTheLoadUnitIsAlreadyThere()
    {
        var route = LiveRoute.Of(Here, MissionTo(Here), [], Plant(Item(Here, DeviceTypes.Automation)));

        Assert.Null(route.Final);
    }

    [Fact]
    public void NoFinalArrowWithoutADestination()
    {
        Assert.Null(LiveRoute.Of(Here, MissionTo(null), [], Plant()).Final);
        Assert.Null(LiveRoute.Of(Here, MissionTo(Guid.Empty), [], Plant()).Final);
    }
}
