namespace Wcs.WebEditor.Drawio.Coordination;

/// <summary>
/// What the page's reread after row 50's write found (<c>DrawioEditor.ReloadPlantItemsAsync</c>,
/// handed to <c>DrawioOperations.OnModeWritten</c>): whether the selected device's panel was cleared
/// and read again - so that what was typed into it is gone -, and the plant reread's own failure when
/// it failed. Both at once, because the panel is cleared even when the plant cannot be read (final
/// review, M2: the line used to say the typing was gone only on the successful path).
/// </summary>
/// <param name="PanelWasReread">A device was selected, and its panel was cleared and read again (or at least cleared, if that read failed too).</param>
/// <param name="PlantRereadFailure">The failure of the plant reread - a transport, host or JSON failure - or null when it went through.</param>
public readonly record struct DrawioModeReread(bool PanelWasReread, Exception? PlantRereadFailure);
