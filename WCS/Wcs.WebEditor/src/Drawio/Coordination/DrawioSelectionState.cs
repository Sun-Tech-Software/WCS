using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Wms;

namespace Wcs.WebEditor.Drawio.Coordination;

public sealed record DrawioSelectionState(
    IReadOnlyList<string>? SelectedCellIds,
    bool SelectionUnavailable,
    string SelectionLine,
    Guid? ConfigItemId,
    PlantItemConfigDto? Config,
    string? ConfigError,
    string ConfigSubjectName,
    string? ConfigWriteError,
    string? ConfigWritten,
    CellPanelKind CellsKind,
    IReadOnlyList<DoorMappingDto>? CellsMappings,
    IReadOnlyList<WmsCell>? WmsCells,
    IReadOnlyList<AutomaticCell>? AutomaticCells,
    string? CellsError,
    string? CellsErrorDetail,
    string? CellsNotice,
    string? CellsWriteError,
    string? CellsWritten,
    string? ReassociableCell,
    string ReassociateTyped,
    string? ReassociateOutcome,
    bool ReassociateWentThrough,
    IReadOnlyList<PlantItemDto>? ReassociationCandidates,
    string ReassociationCandidatesLine,
    long ReassociateRound,
    int Generation,
    // Row 37 of the queue: what came back for the failure each of these lines says - the host's
    // answer as it came, or the failure's type and message - shown behind the "?" next to the line
    // (DrawioFailureLine), as CellsErrorDetail is for CellsError. Null when the line says no failure
    // of a request.
    string? ConfigErrorDetail = null,
    string? ConfigWriteErrorDetail = null,
    string? CellsNoticeDetail = null,
    string? CellsWriteErrorDetail = null,
    string? ReassociateOutcomeDetail = null);

internal sealed record DrawioCanvasRenameResult(
    bool Renamed,
    bool Available,
    string? Failure);

/// <summary>
/// What a cell of the device list (row 58) came to: whether the host took it, and the outcome line
/// the list shows - worded by <c>DrawioSelectionCoordinator.WriteConfigFromListAsync</c>, which knows
/// why a write was refused and what else it touched (the drawing's label, the properties panel).
/// <paramref name="Detail"/> is what came back for a line about a request that failed - the edit permit
/// that could not be asked for, the label the drawing did not take - behind the line's "?" (row 37 of
/// the queue); null otherwise.
/// </summary>
public sealed record DrawioListWriteOutcome(bool Written, string Line, string? Detail = null);
