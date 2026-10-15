using Wcs.Api.Contracts.Dtos;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Wms;

/// <summary>Which of the two cell panels a device gets, if either.</summary>
public enum CellPanelKind
{
    /// <summary>Neither: this kind of device has no warehouse cells to connect.</summary>
    None,

    /// <summary>The door map of a Storage device - many rows, door A and door B (<c>DoorMappings.razor</c>).</summary>
    Doors,

    /// <summary>The one automatic cell of an Automation/CircularDistributor/Loading/Buffer/LoadingAndBuffer device (<c>LinkedCells.razor</c>).</summary>
    LinkedCell,
}

/// <summary>
/// What a page needs to know before it can show <c>DoorMappings</c> or <c>LinkedCells</c>: which of
/// the two a device gets, which warehouse area the candidate cells are read from, and how to build
/// the configuration that carries a new <c>LinkedCellId</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>These three are a second copy, and saying so is the point.</b> <c>PlantCanvas.razor</c> has
/// its own <c>IsLinkedCellDevice</c>, <c>CellAreaTypeFor</c> and <c>CopyConfigWithLinkedCellId</c>,
/// written for task 11 and still there. This file exists because the draw.io page needs the same
/// three answers and the batch that added it may not touch <c>/editor</c>; it is not the shared home
/// of a decision taken once, it is the second of two copies that have to be changed together.
/// <c>CellLinkingMirrorsPlantCanvasTest</c> reads <c>PlantCanvas.razor</c>'s own source and fails
/// when the two sets of device types stop agreeing, so a change made on one side only turns red
/// instead of staying quiet - the same device <c>PropertyModelTest</c> uses for the other pair of
/// hand-kept lists in this project.
/// </para>
/// <para>
/// The boundary itself is not ours: it is the one <c>FrmEDITOR.Private.mnuLinkedStorageCells_Click</c>
/// draws in the WinForms Editor - <c>FrmSTORAGE_ITEM_LINK</c> for a Storage device,
/// <c>FrmAUTOMATIC_ITEM_LINK</c> for the five that have a single cell, and nothing at all for the
/// rest, which that switch lets fall through without doing anything.
/// </para>
/// </remarks>
public static class CellLinking
{
    /// <summary>
    /// The two warehouse area codes the WinForms Editor passes to <c>GetCellsForEditorAsync</c> for
    /// a linkable device (<c>FrmEDITOR.TipiAreaMagazzino</c>: an enum private to that form, not a
    /// shared contract - no type carries them written down anywhere else).
    /// </summary>
    private const short AutomationItemsAreaType = 2;

    private const short BufferItemsAreaType = 3;

    /// <summary>Which panel this device gets: see <see cref="CellPanelKind"/>.</summary>
    public static CellPanelKind KindFor(DeviceTypes deviceType) => deviceType switch
    {
        DeviceTypes.Storage => CellPanelKind.Doors,
        DeviceTypes.Automation or DeviceTypes.CircularDistributor or DeviceTypes.Loading or
            DeviceTypes.Buffer or DeviceTypes.LoadingAndBuffer => CellPanelKind.LinkedCell,
        _ => CellPanelKind.None,
    };

    /// <summary>
    /// The <c>areaType</c> to pass to <c>GetWmsCellsAsync</c> for this device - null for Storage and
    /// for the types that get no panel at all, because <c>FrmSTORAGE_ITEM_LINK</c> passes null and
    /// reads the whole list, with no area filter.
    /// </summary>
    public static short? AreaTypeFor(DeviceTypes deviceType) => deviceType switch
    {
        DeviceTypes.Automation or DeviceTypes.CircularDistributor or DeviceTypes.Loading => AutomationItemsAreaType,
        DeviceTypes.Buffer or DeviceTypes.LoadingAndBuffer => BufferItemsAreaType,
        _ => null,
    };

    /// <summary>
    /// The same configuration with another <c>LinkedCellId</c>: a copy, because the original is the
    /// "how it was" snapshot that <see cref="Editing.LinkBufferCellCommand"/> and
    /// <see cref="Editing.UnlinkBufferCellCommand"/> are handed alongside it, and a field changed in
    /// place would make the two the same object and the undo a no-op.
    /// </summary>
    /// <remarks>
    /// By reflection over the writable properties, and not a constructor with sixty arguments:
    /// <c>PlantItemConfigDto</c> is a mutable DTO with no copy constructor, and a hand-written copy
    /// would silently drop every property added to the contract afterwards - which is the same
    /// silent loss, only slower to notice.
    /// </remarks>
    public static PlantItemConfigDto WithLinkedCellId(PlantItemConfigDto source, decimal linkedCellId)
    {
        var copy = new PlantItemConfigDto();
        foreach (var property in typeof(PlantItemConfigDto).GetProperties().Where(p => p.CanWrite))
            property.SetValue(copy, property.GetValue(source));
        copy.LinkedCellId = linkedCellId;
        return copy;
    }
}
