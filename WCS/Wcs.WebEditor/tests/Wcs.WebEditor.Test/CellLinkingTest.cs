using Wcs.WebEditor.Wms;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// The two questions <c>CellLinking</c> answers for whoever is about to show <c>DoorMappings</c> or
/// <c>LinkedCells</c>: which device types have one automatic cell, and which warehouse area their
/// cells are asked for.
/// </summary>
/// <remarks>
/// <para>
/// <b>This used to be a MIRROR test</b> (<c>CellLinkingMirrorsPlantCanvasTest</c>): it read
/// <c>PlantCanvas.razor</c> as text and asserted that <c>CellLinking</c> gave the same two answers
/// as the old SVG canvas, which had its own copies of them. The canvas is gone, so there is nothing
/// left to mirror - and these answers are now <b>the only</b> ones there are, asked by the draw.io
/// page alone.
/// </para>
/// <para>
/// <b>So the values are pinned instead of compared</b>, and they are the ones the two sides agreed
/// on the day the canvas was deleted - the last moment at which the mirror was green. Pinning is
/// what a single authority needs: with no second copy to disagree with, a silent change to either
/// switch would otherwise be seen by nobody. A type moved from one area to the other fills the
/// panel with cells that cannot be connected, and it fails here as a named difference rather than
/// as an empty panel in front of an operator.
/// </para>
/// </remarks>
public sealed class CellLinkingTest
{
    /// <summary>The five types that carry one automatic cell, and Storage which carries doors instead.</summary>
    [Fact]
    public void TheDeviceTypesWithOneAutomaticCellAreTheFivePinnedHere()
    {
        var linked = Enum.GetValues<DeviceTypes>()
            .Where(type => CellLinking.KindFor(type) is CellPanelKind.LinkedCell)
            .OrderBy(type => type.ToString(), StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            [DeviceTypes.Automation, DeviceTypes.Buffer, DeviceTypes.CircularDistributor,
             DeviceTypes.Loading, DeviceTypes.LoadingAndBuffer],
            linked);

        // Storage is the other half of the same switch, and it is the one that must NOT be in the
        // list above: it gets the doors panel, not the single cell.
        Assert.Equal(CellPanelKind.Doors, CellLinking.KindFor(DeviceTypes.Storage));
    }

    /// <summary>
    /// A type can be on the right list and still be sent to the wrong warehouse area, and that is a
    /// different failure: the panel opens and stays empty, because the cells it asks for are not the
    /// ones this device can reach.
    /// </summary>
    [Fact]
    public void EachTypeAsksTheWarehouseForTheAreaPinnedHere()
    {
        // THE NUMBERS ARE WRITTEN OUT, and the constants they pin are private on purpose: reading
        // them from CellLinking would make this test agree with whatever the switch says today,
        // which is the one thing a pinned value must not do. 2 and 3 are the warehouse's own area
        // codes, and they were these on the day the mirror was deleted.
        Assert.Equal((short)2, CellLinking.AreaTypeFor(DeviceTypes.Automation));
        Assert.Equal((short)2, CellLinking.AreaTypeFor(DeviceTypes.CircularDistributor));
        Assert.Equal((short)2, CellLinking.AreaTypeFor(DeviceTypes.Loading));
        Assert.Equal((short)3, CellLinking.AreaTypeFor(DeviceTypes.Buffer));
        Assert.Equal((short)3, CellLinking.AreaTypeFor(DeviceTypes.LoadingAndBuffer));

        // Storage and everything else ask for no area at all: the whole list, with no filter.
        Assert.Null(CellLinking.AreaTypeFor(DeviceTypes.Storage));
        Assert.Null(CellLinking.AreaTypeFor(DeviceTypes.None));
    }
}
