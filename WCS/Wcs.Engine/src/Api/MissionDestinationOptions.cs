namespace Wcs.Engine.Api;

/// <summary>
/// What a relocator does with the load unit the operator sends to it (item 54, decision 5 of the owner
/// of 13/10): the Editor asks «deposit it, or only bring it there?». The mark stays on the mission from
/// the operator's change until its destination changes by any way (<c>PathMission.RelocatorArrival</c>),
/// and the WCS's database keeps it, so a restart does not lose it.
/// </summary>
/// <remarks>The values are written by hand because they are stored in the WCS's database.</remarks>
public enum RelocatorArrival : byte
{
    /// <summary>No choice: the device reached does what its configuration says, as before item 54.</summary>
    None = 0,

    /// <summary>
    /// Deposit it: arrived at the relocator, the WCS asks the warehouse for a cell the relocator reaches
    /// (the warehouse chooses and reserves it, as for every deposit) and the relocator deposits it there.
    /// </summary>
    Deposit = 1,

    /// <summary>Only bring it there: the load unit arrives at the relocator and stays, nothing else.</summary>
    Stay = 2
}

/// <summary>
/// What goes with the operator's change of destination besides the device and the gateway (item 54,
/// decisions 5 and 6 of the owner of 13/10). The default value is the change of before item 54.
/// </summary>
/// <param name="AtTheRelocator">
/// With a relocator as destination, what it does with the load unit (<see cref="RelocatorArrival"/>);
/// <see cref="RelocatorArrival.None"/> keeps the change of before. Any other value with a destination
/// that is not a relocator is refused (<see cref="MissionDestinationOutcome.NotARelocator"/>).
/// </param>
/// <param name="CellDoorCode">
/// A precise storage cell, by its door A code (owner's answer 7 of 13/10): chosen from the warehouse's
/// list (<see cref="MissionGateway.StorageCellCandidatesAsync"/>) or written by the operator. The
/// destination is then the storage, the group of storages or the relocator the cell is chosen in; the
/// warehouse reserves that cell or refuses it with the reason (<see cref="MissionDestinationOutcome.CellRefused"/>).
/// Not together with <paramref name="AtTheRelocator"/>.
/// </param>
public readonly record struct MissionDestinationOptions(
    RelocatorArrival AtTheRelocator = RelocatorArrival.None,
    string? CellDoorCode = null)
{
    /// <summary>The change of before item 54: no relocator choice, no precise cell.</summary>
    public static MissionDestinationOptions None => default;

    /// <summary>A precise cell, by its door code.</summary>
    public static MissionDestinationOptions Cell(string doorCode) => new(CellDoorCode: doorCode);

    /// <summary>A relocator, and what it does with the load unit.</summary>
    public static MissionDestinationOptions Relocator(RelocatorArrival arrival) => new(arrival);
}

/// <summary>
/// Why a precise cell was refused (<see cref="MissionDestinationOutcome.CellRefused"/>, item 54,
/// decision 6): the warehouse's reasons (<c>StorageCellReservationOutcome</c>) and the WCS's own, said
/// before asking it. The words for the operator are in <see cref="MissionDestinationResult.CellRefusalReason"/>.
/// </summary>
public enum MissionCellRefusal
{
    /// <summary>No cell has this door code: in the WCS's door map, or for the warehouse.</summary>
    UnknownDoor,

    /// <summary>The code is the door B of a cell: only door A codes are accepted (owner's answer 7 of 13/10).</summary>
    NotADoorA,

    /// <summary>The warehouse says the cell is not free, or not suited to this load unit.</summary>
    CellNotAvailable,

    /// <summary>The warehouse does not know the load unit.</summary>
    UnknownLoadUnit,

    /// <summary>
    /// The cell is not in the storage chosen, nor in the chosen group of storages, nor served by the
    /// chosen relocator; or the device chosen is none of the three.
    /// </summary>
    NotInTheChoice
}

/// <summary>What <see cref="MissionGateway.StorageCellCandidatesAsync"/> answers (item 54, decision 6).</summary>
public enum StorageCellCandidatesOutcome
{
    /// <summary>The list: the first 20 of the search of every day, possibly empty.</summary>
    Listed,

    MissionNotFound,
    DeviceNotFound,

    /// <summary>The device chosen is not a storage, a group of storages or a relocator: it has no cells.</summary>
    NotAStorage,

    /// <summary>
    /// Nothing to ask: the load unit has no identifier of the warehouse (<c>LuId</c> 0), or the storages
    /// chosen have no door mapped to a cell in the WCS's database, or their cell has no warehouse.
    /// </summary>
    NoStorageCell,

    /// <summary>The warehouse does not answer, or is not configured.</summary>
    WarehouseUnavailable
}

/// <summary>The answer of <see cref="MissionGateway.StorageCellCandidatesAsync"/>, with the cells when listed.</summary>
public sealed record StorageCellCandidates(StorageCellCandidatesOutcome Outcome,
    IReadOnlyList<Wcs.Wms.Contracts.StorageCellCandidate> Cells, string? Reason = null)
{
    public static StorageCellCandidates Refused(StorageCellCandidatesOutcome outcome, string? reason = null) =>
        new(outcome, [], reason);
}
