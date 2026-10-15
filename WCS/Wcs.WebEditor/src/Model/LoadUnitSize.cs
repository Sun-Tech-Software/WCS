using Wcs.Api.Contracts.Dtos;

namespace Wcs.WebEditor.Model;

/// <summary>
/// How big the load unit on board of a device is drawn, in drawing units (queue item 21). The one
/// place the rule lives: the canvas (<c>drawio-live.js</c>) is handed the result in the live frame
/// and never decides it.
/// </summary>
/// <remarks>
/// The WinForms Editor's rule, measured in <c>DiagramHandler.Private.cs</c>
/// (<c>UpdateItemLoadStatus</c>): the device's <c>LoadUnitImageWidth x LoadUnitImageHeight</c> only
/// when BOTH are greater than zero, otherwise the size already on the node - the palette's
/// <c>LU</c> node, 105 x 105 in <c>WcsPalette.xml</c> (<c>PinOffset (52.5, 52.5)</c>, the same on all
/// ten of them). One positive and one zero is NOT half a size: it is the palette's, as there.
/// </remarks>
public readonly record struct LoadUnitSize(int Width, int Height)
{
    /// <summary>The palette's LU node: what a device without a size of its own shows.</summary>
    public static readonly LoadUnitSize Palette = new(105, 105);

    public static LoadUnitSize Of(int width, int height) => width > 0 && height > 0 ? new(width, height) : Palette;

    public static LoadUnitSize Of(PlantItemConfigDto config) => Of(config.LoadUnitImageWidth, config.LoadUnitImageHeight);
}
