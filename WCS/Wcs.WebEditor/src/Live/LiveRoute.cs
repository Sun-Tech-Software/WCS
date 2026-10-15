using Wcs.Api.Contracts.Dtos;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Live;

/// <summary>Where a load unit is going, from the device it is on: the next step, and the final destination.</summary>
public sealed record LiveRouteView(Guid From, Guid? Next, Guid? Final);

/// <summary>The WinForms Editor's hover route (<c>DiagramHandler.ShowHoverRoute</c>), without the drawing.</summary>
public static class LiveRoute
{
    public static LiveRouteView Of(Guid itemId, MissionDto mission, IEnumerable<MissionStepDto> steps,
        Func<Guid, PlantItemDto?> itemOf)
    {
        var next = steps.FirstOrDefault(step => step.FromNodeId == itemId && step.EndStep == default)?.ToNodeId;
        var final = FinalDestinationOf(mission, itemOf);
        return new LiveRouteView(itemId, next, final == itemId ? null : final);
    }

    /// <summary>The destination, or its relocator when the destination is a warehouse that has one.</summary>
    private static Guid? FinalDestinationOf(MissionDto mission, Func<Guid, PlantItemDto?> itemOf)
    {
        if (mission.DestinationItemId is not { } destination || destination == Guid.Empty) return null;
        var item = itemOf(destination);
        return item is { DeviceType: DeviceTypes.Storage, RelocateItemId: { } relocate } && relocate != Guid.Empty
            ? relocate
            : destination;
    }
}
