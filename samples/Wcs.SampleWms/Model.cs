using Wcs.Wms.Contracts;

namespace Wcs.SampleWms;

// The data a WMS keeps and the WCS asks about. A real WMS has these in its own database, with many
// more columns; the sample keeps only what the WCS -> WMS contract needs, so every field below is
// read or written by at least one route. The classes are mutable on purpose: SampleWarehouse
// changes them under one lock, the way a real adapter would change rows inside a transaction.

/// <summary>A warehouse (a site, or a building of a site). The WCS names it by id.</summary>
public sealed record SampleWarehouseSite(decimal Id, string Description);

/// <summary>
/// What an area is for, as the area type the WCS asks about. The WCS only stores in
/// <see cref="Storage"/> areas. The two bay types are the ones the WCS editor asks for when it links
/// a plant device to a WMS cell (<c>GET cells?areaType=&amp;automaticOnly=true</c>): area type 2 for
/// an Automation, Loading or Circular Distributor device, area type 3 for a Buffer or
/// LoadingAndBuffer device. A bay cell in an area of another type never shows up in that panel.
/// </summary>
public enum SampleAreaKind : short
{
    /// <summary>Storage cells: racks served by a stacker crane, shuttle, ... (area type 0).</summary>
    Storage = 0,

    /// <summary>Cells of automation devices, such as the infeed bay IN01, a Loading device (area type 2).</summary>
    AutomationItems = 2,

    /// <summary>Cells of buffer devices, such as the outfeed bay OUT01, a Buffer device (area type 3).</summary>
    BufferItems = 3
}

/// <summary>
/// An area of a warehouse. <see cref="Automatic"/> areas are the ones the automation reaches: the
/// WCS reads the ids of the automatic <see cref="SampleAreaKind.Storage"/> areas with
/// <c>GET warehouses/{id}/topology</c> and searches a storage cell only there, and the editor lists
/// only cells of automatic areas when it links a bay to a cell. The bays of the sample are automatic.
/// </summary>
public sealed record SampleArea(decimal Id, decimal WarehouseId, string Description, SampleAreaKind Kind, bool Automatic);

/// <summary>
/// A cell: a place that holds one load unit. This is where the WMS and the plant meet, and the three
/// plant fields below must agree with the plant the WCS runs (see samples/plant/sample-plant.json):
/// <list type="bullet">
/// <item><see cref="DoorCodeA"/> is the code the WCS uses to name the cell's door. The WCS's door
/// mappings (<c>PlantGraphDto.DoorMappings</c>) map the same code to the cell id and to the plant
/// device that serves it.</item>
/// <item><see cref="FieldAsi"/>/<see cref="FieldSubitem"/>/<see cref="Partition"/> are the field address of
/// the door: what the WCS sends to the machine that stores into the cell or picks from it.</item>
/// <item><see cref="AccessDeviceIds"/> are the plant devices (storage device and the crane that serves it)
/// through which the cell is reached: the WCS asks for a cell "reachable by these devices".</item>
/// </list>
/// </summary>
public sealed class SampleCell
{
    public required decimal Id { get; init; }
    public required decimal WarehouseId { get; init; }
    public required decimal AreaId { get; init; }
    public required int Column { get; init; }
    public required int Floor { get; init; }
    public required string DoorCodeA { get; init; }

    /// <summary>Empty for a cell with one door, which is every cell of the sample.</summary>
    public string DoorCodeB { get; init; } = string.Empty;

    public required string FieldAsi { get; init; }
    public required string FieldSubitem { get; init; }

    /// <summary>
    /// The name of the cell's only partition. A cell that is not divided in depth positions has one,
    /// named "0000", at <see cref="SampleCell.SinglePositionOffset"/>.
    /// </summary>
    public required string Partition { get; init; }

    /// <summary>How deep the cell is, in millimetres (what the WCS compares a measured depth with).</summary>
    public required int Depth { get; init; }

    /// <summary>
    /// The offset of the only partition of a cell that is not divided in depth positions: -1. When the
    /// WCS looks up a door's field address without a depth (which is what it does for a crane whose
    /// storage depth is fixed, <c>RecordStorageLength</c> = FixedLength), it asks for the partition at
    /// offset -1. A WMS with deep cells lists one partition per depth, with its real offset.
    /// </summary>
    public const int SinglePositionOffset = -1;

    public required IReadOnlyList<Guid> AccessDeviceIds { get; init; }
    public string? Description { get; init; }

    /// <summary>
    /// For a bay cell, the storage areas a load unit can be brought from, in order of priority
    /// (<c>GET cells/{id}/picking-buffer-areas</c>). Empty for a storage cell.
    /// </summary>
    public IReadOnlyList<decimal> PickingAreaIds { get; init; } = [];

    /// <summary>The lock the WCS writes with <c>POST cells/lock</c> (for example "full" when the machine refuses the cell).</summary>
    public CellLockState Locked { get; set; }
}

/// <summary>A type of load unit (pallet, tote, ...). The handling mode is passed to the machines as is.</summary>
public sealed record SampleLoadUnitType(decimal Id, string Description, int HandlingMode);

/// <summary>A load unit the WMS knows: a pallet, a tote, a container, with its code and dimensions.</summary>
public sealed class SampleLoadUnit
{
    public required decimal Id { get; init; }

    /// <summary>The code printed on the label (what a scanner on the field reads).</summary>
    public required string Code { get; init; }

    public required decimal TypeId { get; init; }

    /// <summary>The article on the load unit: what <c>POST load-units/find-by-reference</c> looks for.</summary>
    public string? Reference { get; init; }

    public int Tare { get; init; }
    public int Height { get; init; }
    public int Width { get; init; }
    public int Depth { get; init; }
    public int Weight { get; init; }

    /// <summary>A load unit locked by the WMS (for example under quality control) is not extracted.</summary>
    public bool Locked { get; set; }

    /// <summary>True once the load unit left the automation (<c>POST load-units/release</c>).</summary>
    public bool Shipped { get; set; }
}

/// <summary>
/// A load unit in a cell. <see cref="Reserved"/> is the position the WMS assigned for a storage that
/// has not happened yet; a physical position (Reserved = false) is where the load unit really is.
/// A load unit has at most one physical position and at most one reserved position.
/// </summary>
public sealed class SamplePosition
{
    public required decimal LoadUnitId { get; init; }
    public required decimal CellId { get; init; }
    public required bool Reserved { get; set; }

    /// <summary>The depth the load unit was stored at (millimetres), 0 while reserved.</summary>
    public int Depth { get; set; }
}

/// <summary>
/// The link between a WCS mission and the reserved position it holds (<c>reservations</c>). It is how
/// the WCS asks again for "the cell of my mission" without searching a new one, and the age that
/// lets the WMS free the cells of missions that no longer exist.
/// </summary>
public sealed class SampleStorageHold
{
    public required Guid MissionId { get; init; }
    public required decimal LoadUnitId { get; init; }
    public required decimal CellId { get; init; }
    public required DateTimeOffset RenewedAt { get; set; }
}

/// <summary>Where an extraction row is, seen from the WMS.</summary>
public enum SampleRowState
{
    /// <summary>Not yet taken by the WCS, or offered again after the WCS could not serve it.</summary>
    Waiting,

    /// <summary>The WCS has queued it: a mission will pick the load unit.</summary>
    InExtraction,

    /// <summary>The load unit left its cell for the bay: the row is served.</summary>
    Extracted
}

/// <summary>An extraction order: bring these load units to this bay.</summary>
public sealed class SampleExtractionOrder
{
    public required decimal Id { get; init; }

    /// <summary>The bay cell the load units go to (the WCS maps it to a plant device with its door mappings).</summary>
    public required decimal PickingCellId { get; init; }

    /// <summary>The storage areas the load units can be taken from (from the bay cell's picking areas).</summary>
    public required IReadOnlyList<decimal> BufferAreaIds { get; init; }

    public required List<SampleExtractionRow> Rows { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>An order is closed by the WMS itself when every row is extracted.</summary>
    public bool Closed { get; set; }
}

/// <summary>One load unit of an extraction order.</summary>
public sealed class SampleExtractionRow
{
    public required decimal Id { get; init; }
    public required decimal LoadUnitId { get; init; }
    public SampleRowState State { get; set; }

    /// <summary>The last outcome the WCS reported for the row, as the WCS reported it.</summary>
    public ExtractionRowStatus? LastOutcome { get; set; }
}

/// <summary>One line of the journal: a fact the WCS reported, or a write it asked for, as it arrived.</summary>
public sealed record SampleJournalEntry(DateTimeOffset At, string Route, string What);
