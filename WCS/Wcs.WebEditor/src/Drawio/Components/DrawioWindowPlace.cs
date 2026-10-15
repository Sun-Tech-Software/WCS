namespace Wcs.WebEditor.Drawio.Components;

/// <summary>
/// Where a floating window of the draw.io page opens when this browser remembers nothing about it,
/// in CSS pixels of the viewport. The module (drawio-windows.js) brings it back inside the viewport
/// anyway, so a place meant for a large screen is still usable on a small one.
/// </summary>
public sealed record DrawioWindowPlace(int Left, int Top, int Width, int Height)
{
    /// <summary>
    /// The window at <paramref name="index"/> in the strip's order, a step down and to the right of
    /// the one before: opened together from an empty memory, no window hides another's title bar.
    /// </summary>
    public static DrawioWindowPlace Cascade(int index, int width, int height) =>
        new(40 + 32 * index, 140 + 32 * index, width, height);
}
