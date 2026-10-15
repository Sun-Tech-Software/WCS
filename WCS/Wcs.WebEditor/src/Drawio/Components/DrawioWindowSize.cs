namespace Wcs.WebEditor.Drawio.Components;

/// <summary>
/// The useful outer bounds of an automatically sized draw.io window. JavaScript measures the
/// rendered content and uses these values as the preferred width and the maximum useful height;
/// the viewport can still make either dimension smaller.
/// </summary>
public sealed record DrawioWindowSize(int Width, int Height);

