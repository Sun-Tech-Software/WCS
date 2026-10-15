using System.Net;
using System.Text.Json;
using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Drawing;
using Wcs.WebEditor.Editing;
using Wcs.WebEditor.Model;
using Wcs.WebEditor.Session;
using Wcs.WebEditor.Wms;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Drawio.Coordination;

internal sealed class DrawioSelectionCoordinator(
    EditorSessionState session,
    DrawioPlantWriteGate writeGate,
    Func<PlantGraph?> graph,
    Dictionary<string, Guid> plantItemByDrawnCell,
    Func<Task<IReadOnlyList<DrawioMarkedCell>?>> readMarkedCells,
    Func<string, string?, bool, Task<DrawioCanvasRenameResult>> renameCell,
    Func<Task<DrawioSaid?>> ensureWritable,
    Func<NavigatorStatusTypes?> engineStatus)
{
    private IReadOnlyList<string>? _selectedCellIds;
    private bool _selectionUnavailable;

    /// <summary>
    /// Queue item 15: how many cells draw.io said were selected when there were too many to follow -
    /// their identifiers would not fit in one message of the circuit (drawio-bridge.js, tellOrWarn).
    /// Null when the page knows what is selected, or knows that it cannot know.
    /// </summary>
    private int? _unfollowedSelection;
    private Guid? _configItemId;
    private PlantItemConfigDto? _selectedConfig;
    private DrawioSaid? _configError;
    private DrawioSaid? _configWriteError;
    private string? _configWritten;
    private int _generation;
    private IReadOnlyList<DoorMappingDto>? _cellsMappings;
    private IReadOnlyList<WmsCell>? _cellsWmsCells;
    private IReadOnlyList<AutomaticCell>? _cellsAutomaticCells;
    private string? _cellsError;
    private string? _cellsErrorDetail;
    private DrawioSaid? _cellsNotice;
    private DrawioSaid? _cellsWriteError;
    private string? _cellsWritten;
    private IReadOnlyList<DrawioMarkedCell>? _markedCells;
    private string _reassociateTyped = "";
    private DrawioSaid? _reassociateOutcome;
    private bool _reassociateWentThrough;
    private long _reassociateRound;

    public event Action? Changed;

    public DrawioSelectionState State => new(
        _selectedCellIds,
        _selectionUnavailable,
        SelectionLine,
        _configItemId,
        _selectedConfig,
        _configError?.Text,
        ConfigSubjectName,
        _configWriteError?.Text,
        _configWritten,
        SelectedCellsKind,
        _cellsMappings,
        _cellsWmsCells,
        _cellsAutomaticCells,
        _cellsError,
        _cellsErrorDetail,
        _cellsNotice?.Text,
        _cellsWriteError?.Text,
        _cellsWritten,
        ReassociableCell,
        _reassociateTyped,
        _reassociateOutcome?.Text,
        _reassociateWentThrough,
        ReassociationCandidates,
        ReassociationCandidatesLine,
        _reassociateRound,
        _generation,
        _configError?.Detail,
        _configWriteError?.Detail,
        _cellsNotice?.Detail,
        _cellsWriteError?.Detail,
        _reassociateOutcome?.Detail);

    public Task SelectionChangedAsync(IReadOnlyList<string> cellIds) => FollowSelectionAsync(cellIds, unfollowed: null);

    /// <summary>
    /// Queue item 15: draw.io selected <paramref name="count"/> cells, too many for their identifiers to
    /// cross the circuit. The page follows none of them: no cell is the one the panel is about, and
    /// the line says how many were selected and why the panel is empty.
    /// </summary>
    public Task SelectionTooLargeToFollowAsync(int count) => FollowSelectionAsync([], count);

    private async Task FollowSelectionAsync(IReadOnlyList<string> cellIds, int? unfollowed)
    {
        _selectionUnavailable = false;
        _unfollowedSelection = unfollowed;
        var before = _selectedCellIds;
        _selectedCellIds = cellIds;
        if (before is null || !before.SequenceEqual(_selectedCellIds, StringComparer.Ordinal))
        {
            _reassociateTyped = "";
            _reassociateOutcome = null;
            _reassociateWentThrough = false;
        }

        Notify();
        await RefreshSelectedConfigAsync();
        if (_selectedCellIds is { Count: 1 } ids && DeviceOf(ids[0]) is null)
        {
            _markedCells = await readMarkedCells();
            Notify();
        }
        else
        {
            _markedCells = null;
        }
    }

    public void SelectionUnavailable()
    {
        _selectionUnavailable = true;
        _unfollowedSelection = null;
        _selectedCellIds = null;
        ForgetSelectedConfig();
        Notify();
    }

    public void SetReassociateTyped(string value)
    {
        _reassociateTyped = value;
        Notify();
    }

    public async Task ReassociateAsync()
    {
        try
        {
            await PointTheShapeAsync();
        }
        finally
        {
            _reassociateRound++;
            Notify();
        }
    }

    public async Task SaveConfigAsync(PlantItemConfigDto edited)
    {
        _configWriteError = null;
        _configWritten = null;
        if (_selectedConfig is not { } before || graph() is not { } current)
        {
            _configWriteError = "This page is no longer showing that device's configuration (the selection moved on while the confirmation was on its way): nothing was written. Select the device again and confirm once more.";
            Notify();
            return;
        }

        if (edited.ItemId != before.ItemId || _configItemId != before.ItemId)
        {
            _configWriteError = "The selection changed while this was being confirmed: nothing was written. Select the device again and confirm once more.";
            Notify();
            return;
        }

        // D3: the basis of "what the operator changed" is the panel's read with the fields this panel
        // has WRITTEN since that read put on it - see TakeWhatWasWritten.
        var basis = _writtenOver is { } over && ReferenceEquals(over.ReadFrom, before) ? over.Basis : before;
        var outcome = await WriteConfigAsync(DrawioPlantWriter.Panel, DrawioWriteRefusal.ForPanel, basis, edited, current);
        switch (outcome.Kind)
        {
            case ConfigWriteKind.Refused:
                _configWriteError = outcome.Refusal;
                break;
            case ConfigWriteKind.HostRefused:
                _configWriteError = DrawioSaid.Failed(
                    $"The configuration was NOT written because the host refused it - {HostRefusalMeaning((WcsApiException)outcome.Failure!)}: nothing reached the plant, and the values are still here.",
                    outcome.Failure!);
                break;
            case ConfigWriteKind.EngineNotStopped:
                _configWriteError = $"The configuration was NOT written: {outcome.Refusal!.Text}. Nothing reached the plant; the values are still here.";
                break;
            case ConfigWriteKind.NotRead:
                _configWriteError = DrawioSaid.Failed(
                    "The configuration was NOT written: the device's current configuration could not be read from the host just before writing " +
                    $"because {DrawioFailureWords.Why(outcome.Failure!)}, and without it this page cannot keep what others changed on it - nothing reached the plant, and the values are still here.",
                    outcome.Failure!);
                break;
            case ConfigWriteKind.ChangedOnTheHost:
                _configWriteError = $"The configuration was NOT written: {ConflictWords(outcome.Conflicts!, "this panel")}. " +
                                    "Nothing was written, so as not to overwrite it; the panel has been read again from the host (anything typed into it and not confirmed is gone) - confirm again if you still want your value.";
                Notify();
                await RefreshConfigAsync(force: true);
                break;
            case ConfigWriteKind.Gone:
                _configWriteError =
                    "The configuration was NOT written: the host does not have this device any more - somebody deleted it while this panel was open. " +
                    "Nothing was brought back to life and nothing was half-written. The values are still here; reload the page before doing anything else with them.";
                break;
            case ConfigWriteKind.WrittenNotReadBack:
                _configWriteError = DrawioSaid.Failed(
                    $"The configuration was written, but the device could not be read back afterwards because {WhyNotReadBack(outcome.Failure!)}: reload the page before changing anything else.",
                    outcome.Failure!);
                TakeWhatWasWritten(outcome, before);
                break;
            case ConfigWriteKind.Unknown:
                _configWriteError = DrawioSaid.Failed(DrawioFailureWords.RefusalMeaning(outcome.Failure!) is { } refused
                    ? $"Writing the configuration stopped because the host refused one of its two requests - {refused}: if it was the write, nothing was written; " +
                      "if it was the reading back after it, the configuration was written. The values are still here, so check the device before confirming again."
                    : $"Writing the configuration failed because {DrawioFailureWords.WhyAWriteFailed(outcome.Failure!)}, and whether it went through is NOT known - " +
                      "the request may have reached the host anyway; the values are still here, so check the device before confirming again.",
                    outcome.Failure!);
                break;
            case ConfigWriteKind.Written:
                _configWritten = $"Written: the configuration of {ConfigSubjectName} is on the plant.";
                TakeWhatWasWritten(outcome, before);
                // Only when there IS something to say about the label: a refusal of a second press that
                // landed while this write was on its way is still true, and stays on screen next to it.
                if (outcome.CanvasTrouble is { } trouble) _configWriteError = trouble;
                break;
        }

        Notify();
    }

    /// <summary>
    /// D3: after a write that went through, the basis the panel compares its next confirmation against
    /// is its READ with only the fields it has WRITTEN since put on it (<see cref="ConfigWriteResult.WrittenBasis"/>).
    /// Those fields must compare against what was written - otherwise a second change of the same field
    /// would look changed on the host by the first write, and be refused as a conflict with the
    /// operator's own write. Every OTHER field keeps comparing against the read: the panel's working
    /// copy still holds the read's values for the fields the operator never touched, and a basis made
    /// of the whole written configuration - which carries what OTHER clients had changed, merged in -
    /// would make those untouched fields look changed and send the read's old values back
    /// (confirmation review, I-A: a silent revert on the second confirmation).
    /// Kept next to the read it was written over, and not put in place of it: the panel copies its
    /// working values from that read BY REFERENCE (PropertyPanel.OnParametersSet), and a new reference
    /// would throw away what the operator typed while the write was on its way. A new read of the
    /// panel is a new reference, and makes this stale by itself.
    /// </summary>
    private void TakeWhatWasWritten(ConfigWriteResult outcome, PlantItemConfigDto readFrom)
    {
        if (outcome.WrittenBasis is { } basis) _writtenOver = (readFrom, basis);
    }

    private (PlantItemConfigDto ReadFrom, PlantItemConfigDto Basis)? _writtenOver;

    /// <summary>
    /// A cell of the device list (row 58, compito 2): the SAME write the properties panel makes -
    /// <see cref="WriteConfigAsync"/>, the page's write gate with the list's own writer and refusals,
    /// the edit permit taken at the moment, <see cref="EditItemConfigCommand"/>, the drawing told of a
    /// new name - worded for the list, which names the change in <paramref name="change"/>.
    /// </summary>
    /// <remarks>
    /// Coherence with the panel: when the device written is the one the panel is showing, the panel
    /// reads it again (<see cref="RefreshConfigAsync"/>, forced - the selection did not move), which
    /// throws away whatever was typed there and not confirmed; the outcome says so with the words the
    /// cell-linking gestures and the connection mode switch already use. The other direction - the
    /// list reading the plant again after a write from the panel - is the list's own business: every
    /// configuration write of this page ends in <see cref="PlantGraph.AddItem"/> or in a whole
    /// reread of the plant, and the list follows <see cref="PlantGraph.TopologyRevision"/>.
    /// </remarks>
    public async Task<DrawioListWriteOutcome> WriteConfigFromListAsync(PlantItemConfigDto before, PlantItemConfigDto edited, string change)
    {
        if (graph() is not { } current)
            return new(false, $"Not written - {change}: the plant is not loaded on this page. Reload the page.");

        var outcome = await WriteConfigAsync(DrawioPlantWriter.DeviceList, DrawioWriteRefusal.ForDeviceList, before, edited, current);
        switch (outcome.Kind)
        {
            case ConfigWriteKind.Refused:
                return new(false, $"Not written - {change}: {outcome.Refusal!.Text}", outcome.Refusal.Detail);
            case ConfigWriteKind.HostRefused:
                return new(false,
                    $"Not written - {change}: the host refused it - {HostRefusalMeaning((WcsApiException)outcome.Failure!)}. Nothing reached the plant; the row shows what the plant holds.",
                    DrawioFailureWords.Detail(outcome.Failure!));
            case ConfigWriteKind.EngineNotStopped:
                return new(false, $"Not written - {change}: {outcome.Refusal!.Text}. Nothing reached the plant.");
            // Final review, C17: the list says the shared write's failures as the panel does - the why
            // in words, what came back behind the line's "?" - where it used to quote the failure's
            // message in the middle of the sentence.
            case ConfigWriteKind.NotRead:
                return new(false,
                    $"Not written - {change}: the device's current configuration could not be read from the host just before writing because {DrawioFailureWords.Why(outcome.Failure!)}, " +
                    "and without it this page cannot keep what others changed on it. Nothing reached the plant.",
                    DrawioFailureWords.Detail(outcome.Failure!));
            case ConfigWriteKind.ChangedOnTheHost:
                return new(false, $"Not written - {change}: {ConflictWords(outcome.Conflicts!, "this list")}. " +
                                  "Nothing was written, so as not to overwrite it; the row shows what the plant holds now - write again if you still want your value.");
            case ConfigWriteKind.Gone:
                return new(false,
                    $"Not written - {change}: the host does not have this device any more - somebody deleted it while this list was open. " +
                    "Nothing was brought back to life and nothing was half-written. Reload the page.");
            case ConfigWriteKind.WrittenNotReadBack:
                return new(true,
                    $"Written - {change} - but the device could not be read back afterwards because {WhyNotReadBack(outcome.Failure!)}: reload the page before changing anything else.",
                    DrawioFailureWords.Detail(outcome.Failure!));
            case ConfigWriteKind.Unknown:
                return new(false, DrawioFailureWords.RefusalMeaning(outcome.Failure!) is { } refused
                        ? $"It is NOT known whether {change} went through: the host refused one of its two requests - {refused} - and if it was the write, nothing was written; " +
                          "if it was the reading back after it, it was. The row shows what the plant holds once the list is read again; check it before trying again."
                        : $"It is NOT known whether {change} went through, because {DrawioFailureWords.WhyAWriteFailed(outcome.Failure!)} - the request may have reached the host anyway. " +
                          "The row shows what the plant holds once the list is read again; check it before trying again.",
                    DrawioFailureWords.Detail(outcome.Failure!));
        }

        var line = $"Written: {change} is on the plant.";
        if (outcome.CanvasTrouble is { } trouble) line += $" {trouble.Text}";
        if (_configItemId == edited.ItemId && await RefreshConfigAsync(force: true))
            line += " The properties panel shows this device and has been read again from the host, so anything typed into it and not confirmed is gone.";
        return new(true, line, outcome.CanvasTrouble?.Detail);
    }

    /// <summary>
    /// The one configuration write of this page, for the panel and for the device list alike: the
    /// write gate with the caller's own writer (and its refusal if another writer holds it), the edit
    /// permit asked for NOW, <see cref="EditItemConfigCommand"/>, and - still inside the gate - the
    /// drawing told of a new name. It classifies what happened; the words are the caller's.
    /// </summary>
    /// <remarks>
    /// Owner's decisions of 27/9, inside the gate after the permit (<see cref="ApplyConfigAsync"/>):
    /// D1, a change of ConnectionType or DeviceConnectionId is refused unless the engine is stopped
    /// (the host has the same rule); D3, fetch-then-apply (<see cref="ConfigFetchThenApply"/>) - the
    /// device read again, only the fields the operator changed put on top of it, and a field changed
    /// on both sides refused and named instead of overwritten.
    /// </remarks>
    private async Task<ConfigWriteResult> WriteConfigAsync(
        DrawioPlantWriter writer,
        Func<DrawioPlantWriter, string> refusalFor,
        PlantItemConfigDto before,
        PlantItemConfigDto edited,
        PlantGraph current)
    {
        ConfigWriteResult? result = null;
        var refused = await writeGate.WriteThroughAsync(writer, refusalFor, ensureWritable,
            async () => result = await ApplyConfigAsync(before, edited, current));
        return refused is not null ? new(ConfigWriteKind.Refused, Refusal: refused) : result!;
    }

    /// <summary>The configuration command and the drawing told of a new name, classified - inside the gate, see <see cref="WriteConfigAsync"/>.</summary>
    private async Task<ConfigWriteResult> ApplyConfigAsync(PlantItemConfigDto before, PlantItemConfigDto edited, PlantGraph current)
    {
        var changed = ConfigFetchThenApply.Changed(before, edited);

        // D1: the engine's state is the one EnsureWritableAsync has just read for this very write.
        if (changed.Any(name => name is nameof(PlantItemConfigDto.ConnectionType) or nameof(PlantItemConfigDto.DeviceConnectionId))
            && engineStatus() is { } status && status != NavigatorStatusTypes.Stop)
            return new(ConfigWriteKind.EngineNotStopped, Refusal:
                $"the engine is {EngineStateWords(status)}, and a device's ConnectionType and DeviceConnectionId are changed only with the engine stopped - " +
                "the engine gives a device the simulator or the PLC only when it opens the connections, and resuming from Pause does not reopen them");

        // D3: the device as the host holds it NOW - a GET, so a failure here wrote nothing.
        PlantItemConfigDto? onTheHost;
        try
        {
            onTheHost = await session.Client.GetItemConfigurationAsync(before.ItemId);
        }
        catch (Exception ex) when (ex is HttpRequestException or WcsApiException or TaskCanceledException or JsonException)
        {
            return new(ConfigWriteKind.NotRead, Failure: ex);
        }

        if (onTheHost is null) return new(ConfigWriteKind.Gone);
        if (ConfigFetchThenApply.Conflicts(changed, before, edited, onTheHost) is { Count: > 0 } conflicts)
            return new(ConfigWriteKind.ChangedOnTheHost, Conflicts: conflicts);
        var merged = ConfigFetchThenApply.Merge(changed, edited, onTheHost);

        try
        {
            await new EditItemConfigCommand(onTheHost, merged).ApplyAsync(session.Client, current);
        }
        catch (WcsApiException gone) when (gone.StatusCode == HttpStatusCode.NotFound)
        {
            return new(ConfigWriteKind.Gone);
        }
        catch (WcsApiException refused) when (refused.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.Locked)
        {
            // A definite "no" from the host on the PUT (the reread after it is a GET, which neither
            // answers): 409 is the engine's state forbidding it now - gone into Run, Recover or
            // Dispose after EnsureWritableAsync's own check, or a connection change outside Stop
            // (PlantQueries.RequireNotRunning/RequireStopped) -, 423 the edit permit lost in that same
            // window (EditPermissionFilter). Nothing was written - not "it is not known".
            return new(ConfigWriteKind.HostRefused, Failure: refused);
        }
        catch (InvalidOperationException ex)
        {
            return new(ConfigWriteKind.WrittenNotReadBack, Failure: ex, WrittenBasis: ConfigFetchThenApply.Merge(changed, edited, before));
        }
        catch (JsonException ex)
        {
            // The PUT reads no answer (WcsClient.SaveItemConfigurationAsync only checks the status),
            // so an unreadable body can only be the reread's: the write went through.
            return new(ConfigWriteKind.WrittenNotReadBack, Failure: ex, WrittenBasis: ConfigFetchThenApply.Merge(changed, edited, before));
        }
        catch (Exception ex) when (ex is HttpRequestException or WcsApiException or TaskCanceledException)
        {
            return new(ConfigWriteKind.Unknown, Failure: ex);
        }

        var renamed = !string.Equals(onTheHost.Name, merged.Name, StringComparison.Ordinal);
        return new(ConfigWriteKind.Written, CanvasTrouble: renamed ? await RenameConfigOnCanvasAsync(before.ItemId, merged.Name) : null,
            WrittenBasis: ConfigFetchThenApply.Merge(changed, edited, before));
    }

    private static string EngineStateWords(NavigatorStatusTypes status) => status switch
    {
        NavigatorStatusTypes.Run => "running",
        NavigatorStatusTypes.Pause => "paused",
        NavigatorStatusTypes.Recover => "recovering",
        NavigatorStatusTypes.Dispose => "shutting down",
        _ => status.ToString(),
    };

    private static string ConflictWords(IReadOnlyList<ConfigFetchThenApply.Conflict> conflicts, string reader) =>
        string.Join("; ", conflicts.Select(conflict =>
            $"{conflict.Label} was changed on the host since {reader} read it - the host now holds {conflict.OnTheHost}, and you wrote {conflict.Wanted}"));

    private enum ConfigWriteKind
    {
        Written,
        Refused,

        /// <summary>The host answered the PUT with 409 or 423: refused, nothing written (<see cref="HostRefusalMeaning"/>).</summary>
        HostRefused,
        Gone,
        WrittenNotReadBack,
        Unknown,

        /// <summary>D1: a connection field changed with the engine not stopped - refused by the page, nothing sent.</summary>
        EngineNotStopped,

        /// <summary>D3: the device could not be read just before writing - nothing sent.</summary>
        NotRead,

        /// <summary>D3: a field the operator changed was changed on the host too - refused, nothing sent.</summary>
        ChangedOnTheHost,
    }

    private sealed record ConfigWriteResult(
        ConfigWriteKind Kind,
        DrawioSaid? Refusal = null,
        Exception? Failure = null,
        DrawioSaid? CanvasTrouble = null,
        IReadOnlyList<ConfigFetchThenApply.Conflict>? Conflicts = null,
        PlantItemConfigDto? WrittenBasis = null);

    public Task SaveDoorMappingsAsync(IReadOnlyList<DoorMappingDto> wanted) => SaveDoorMappingsCoreAsync(wanted);
    public Task ClearDoorMappingsAsync() => ClearDoorMappingsCoreAsync();
    public Task LinkCellAsync(WmsCell cell) => LinkCellCoreAsync(cell);
    public Task UnlinkCellAsync() => UnlinkCellCoreAsync();
    public async Task RefreshSelectedConfigAsync() => await RefreshConfigAsync();

    /// <summary>
    /// Rereads the selected device's configuration from the host even when the SAME device stays
    /// selected (fix round 1, CRITICAL 1). <see cref="RefreshSelectedConfigAsync"/> is built for a
    /// selection that MOVED - its own guard below skips the read outright when the target has not
    /// changed and a config is already in hand, which is exactly wrong for a write ELSEWHERE on the
    /// plant that can invalidate what this panel is showing without the selection moving at all: the
    /// plant-wide connection mode switch (row 50) is the first of these. Without a forced reread,
    /// the panel kept its stale <c>ConnectionType</c>, and the next save on ANY other field sent that
    /// stale value back - silently undoing the switch on whichever device happened to be selected.
    /// </summary>
    /// <returns>
    /// True when a device WAS selected (so its panel really was cleared and reread, or at least
    /// cleared if the reread itself failed) - false when nothing was selected and so there was
    /// nothing to discard. Fix round 2, IMPORTANT 1: the caller (<c>DrawioOperations.ConfirmModeAsync</c>)
    /// uses this to decide whether row 50's own outcome line needs to say that whatever the operator
    /// had typed into the panel, unconfirmed, is gone - the same warning
    /// <see cref="LinkCellCoreAsync"/>/<see cref="UnlinkCellCoreAsync"/> already give after their own
    /// reread.
    /// </returns>
    public Task<bool> ForceRefreshSelectedConfigAsync() => RefreshConfigAsync(force: true);

    public PlantItemDto? DeviceOf(string cellId)
    {
        if (graph() is not { } current) return null;
        if (Guid.TryParse(cellId, out var itemId)
            && current.Items.FirstOrDefault(item => item.ItemId == itemId) is { } marked) return marked;

        return plantItemByDrawnCell.TryGetValue(cellId, out var mapped)
            ? current.Items.FirstOrDefault(item => item.ItemId == mapped)
            : null;
    }

    public string DrawnCellOf(Guid itemId)
    {
        foreach (var (cellId, mapped) in plantItemByDrawnCell)
            if (mapped == itemId) return cellId;
        return itemId.ToString();
    }

    private PlantItemDto? SelectedDevice =>
        !_selectionUnavailable && _selectedCellIds is { Count: 1 } ids ? DeviceOf(ids[0]) : null;

    private CellPanelKind SelectedCellsKind =>
        _selectedConfig is { } config ? CellLinking.KindFor((DeviceTypes)config.DeviceType) : CellPanelKind.None;

    private string? ReassociableCell =>
        !_selectionUnavailable && _selectedCellIds is { Count: 1 } ids && DeviceOf(ids[0]) is null
        && _markedCells?.Any(cell => cell.Id == ids[0] && cell.Mark == CellReassociation.DeviceMark) == true
            ? ids[0]
            : null;

    private IReadOnlyList<PlantItemDto>? ReassociationCandidates =>
        graph() is { } current
            ? CellReassociation.DevicesNoCellStandsFor(current.Items, _markedCells, plantItemByDrawnCell)
            : null;

    private string ReassociationCandidatesLine => ReassociationCandidates is not { } candidates
        ? "The plant is not loaded: there is nothing to point this shape at yet."
        : candidates.Count switch
        {
            0 => "No device of the plant is without a shape",
            1 => "1 device of the plant has no shape on this canvas",
            var many => $"{many} devices of the plant have no shape on this canvas",
        };

    private string ConfigSubjectName =>
        _configItemId is not { } itemId ? ""
        : graph()?.Items.FirstOrDefault(item => item.ItemId == itemId)?.Name is { Length: > 0 } name ? name
        : itemId.ToString();

    private string SelectionLine
    {
        get
        {
            if (_unfollowedSelection is { } unfollowed)
                return $"Selected: {unfollowed} cells, too many to follow: the panel shows none of them.";
            var ids = _selectedCellIds ?? [];
            if (ids.Count == 0) return "Nothing selected.";
            if (ids.Count == 1)
                return DeviceOf(ids[0]) is { } only
                    ? $"Selected: {NameOf(only)}"
                    : "Selected: one cell that is not a device of the plant.";
            var devices = ids.Count(id => DeviceOf(id) is not null);
            return $"Selected: {ids.Count} cells ({devices} {(devices == 1 ? "device" : "devices")} of the plant).";
        }
    }

    private async Task PointTheShapeAsync()
    {
        _reassociateOutcome = null;
        _reassociateWentThrough = false;
        if (ReassociableCell is not { } cellId)
        {
            _reassociateOutcome = "The selection moved on while this gesture was on its way, and this box is no longer about the shape it was about. Nothing was written - select the shape again.";
            return;
        }

        if (CellReassociation.Parse(_reassociateTyped, out var refusal) is not { } deviceId)
        {
            _reassociateOutcome = refusal;
            return;
        }

        PlantItemDto? device;
        try
        {
            device = await session.Client.GetItemAsync(deviceId);
        }
        catch (Exception ex) when (ex is HttpRequestException or WcsApiException or TaskCanceledException)
        {
            _reassociateOutcome = DrawioSaid.Failed($"The host could not be asked whether it has a device {deviceId} because {DrawioFailureWords.Why(ex)}, so nothing was written.", ex);
            return;
        }

        _markedCells = await readMarkedCells();
        if (CellReassociation.WhyNot(cellId, deviceId, device, _markedCells, plantItemByDrawnCell) is { } why)
        {
            _reassociateOutcome = why;
            return;
        }

        plantItemByDrawnCell[cellId] = deviceId;
        _reassociateTyped = "";
        _reassociateWentThrough = true;
        var renamed = await DescribeReassociationRenameAsync(cellId, device!.Name);
        _reassociateOutcome = new DrawioSaid(
            CellReassociation.WhatItMeans(device!, graph()?.TypesByDeviceType.ContainsKey(device!.DeviceType) == true) + renamed.Words,
            renamed.Failure);
        await RefreshConfigAsync();
    }

    /// <returns>
    /// The words that end the outcome line, and the drawing's own failure when there was one: behind
    /// the line's "?" (row 37 of the queue), not in the middle of it.
    /// </returns>
    private async Task<(string Words, string? Failure)> DescribeReassociationRenameAsync(string cellId, string? name)
    {
        // alreadyOnThePlant: false. Re-pointing only writes the pair into plantItemByDrawnCell
        // (WhatItMeans above says so: "Nothing has reached the plant yet") - the plant learns of it
        // only at the next save - so this label change is real unsaved work, not an echo, and must
        // light the "changed since last save" mark (row 46) rather than be suppressed by it.
        var outcome = await renameCell(cellId, name, false);
        if (outcome.Renamed) return ("", null);
        if (!outcome.Available)
            return (" The drawing could not be told, so the shape still carries its old label until the page is reloaded.",
                outcome.Failure is { Length: > 0 } failure ? failure : null);
        return (" The drawing did not take the new label (the cell is no longer there, or the selection could not be followed): the shape still carries its old one until the page is reloaded.", null);
    }

    /// <returns>
    /// True when <paramref name="force"/> is set AND a device is (still) selected - see
    /// <see cref="ForceRefreshSelectedConfigAsync"/>'s own remarks; false in every other case,
    /// including the plain selection-changed path <see cref="RefreshSelectedConfigAsync"/> uses,
    /// which never reads this value.
    /// </returns>
    private async Task<bool> RefreshConfigAsync(bool force = false)
    {
        var target = SelectedDevice?.ItemId;
        if (!force && _configItemId == target && (_selectedConfig is not null || target is null)) return false;
        var generation = ++_generation;
        if (_configItemId != target)
        {
            _configItemId = target;
            _selectedConfig = null;
            _configError = null;
            _configWriteError = null;
            _configWritten = null;
            ForgetSelectedCells();
            Notify();
        }
        else if (force && target is not null)
        {
            // Same device, forced: the config in hand may already be stale (that is the whole point
            // of being called), so it is cleared before the read that replaces it - exactly the state
            // a moved-selection reread already shows while it is in flight, and what a reread that
            // then fails leaves behind (below) instead of a stale value nobody can tell is stale.
            _selectedConfig = null;
            Notify();
        }

        if (target is not { } itemId) return false;
        try
        {
            var config = await session.Client.GetItemConfigurationAsync(itemId);
            if (generation != _generation) return force;
            _selectedConfig = config;
            _configError = config is null
                ? $"The configuration could not be read: {itemId} is not (any more) on the host."
                : null;
        }
        catch (Exception ex)
        {
            // Row 37 of the queue: the why in words - an unforeseen failure too -, what came back behind the "?".
            if (generation != _generation) return force;
            _selectedConfig = null;
            _configError = DrawioSaid.Failed($"The configuration of this device could not be read because {DrawioFailureWords.Why(ex)}.", ex);
        }

        Notify();
        if (_selectedConfig is not { } read) return force;
        try
        {
            await RefreshSelectedCellsAsync(itemId, read, generation);
        }
        catch (Exception ex)
        {
            if (generation != _generation) return force;
            ForgetSelectedCellsPanel();
            _cellsError = CellsNotShown("reading them stopped on an unforeseen failure");
            _cellsErrorDetail = DrawioFailureWords.Detail(ex);
            Notify();
        }

        return force;
    }

    private void ForgetSelectedConfig()
    {
        _generation++;
        _configItemId = null;
        _selectedConfig = null;
        _configError = null;
        _configWriteError = null;
        _configWritten = null;
        ForgetSelectedCells();
    }

    private void ForgetSelectedCells()
    {
        ForgetSelectedCellsPanel();
        _cellsWriteError = null;
        _cellsWritten = null;
    }

    private void ForgetSelectedCellsPanel()
    {
        _cellsMappings = null;
        _cellsWmsCells = null;
        _cellsAutomaticCells = null;
        _cellsError = null;
        _cellsErrorDetail = null;
        _cellsNotice = null;
    }

    private async Task RefreshSelectedCellsAsync(Guid itemId, PlantItemConfigDto config, int generation)
    {
        var deviceType = (DeviceTypes)config.DeviceType;
        var kind = CellLinking.KindFor(deviceType);
        if (kind is CellPanelKind.None) return;
        var mappingsTask = session.Client.GetDoorMappingsAsync();
        var cellsTask = session.Client.GetWmsCellsAsync(CellLinking.AreaTypeFor(deviceType), kind is CellPanelKind.LinkedCell);
        var automaticCellsTask = kind is CellPanelKind.LinkedCell ? session.Client.GetAutomaticCellsAsync() : null;
        IReadOnlyList<DoorMappingDto>? mappings = null;
        IReadOnlyList<WmsCell>? cells = null;
        IReadOnlyList<AutomaticCell>? automaticCells = null;
        // Row 29 of the queue: what failed and why, in words, goes into the sentence; the host's answer
        // as it came goes behind the "?" next to it (DrawioPropertyDock). Until then the answer sat in
        // the middle of the sentence - the engine's 503 problem document, JSON and trace id, in red
        // between "the warehouse's cell list (" and ") could not be read". Each read carries its own
        // why: with both failed, one why after both would have blamed the warehouse for the door map
        // too, which is the WCS's own store and which a warehouse that does not answer never touches.
        var failures = new List<string>();
        var details = new List<string>();
        DrawioSaid? notice = null;
        try { mappings = (await mappingsTask).Where(mapping => mapping.ItemId == itemId).ToList(); }
        catch (Exception ex) when (ex is HttpRequestException or WcsApiException or TaskCanceledException)
        {
            failures.Add($"the door map of this device could not be read because {DrawioFailureWords.Why(ex)}");
            details.Add($"The door map of this device: {DrawioFailureWords.Detail(ex)}");
        }
        try { cells = await cellsTask; }
        catch (Exception ex) when (ex is HttpRequestException or WcsApiException or TaskCanceledException)
        {
            failures.Add($"the warehouse's cell list could not be read because {DrawioFailureWords.Why(ex)}");
            details.Add($"The warehouse's cell list: {DrawioFailureWords.Detail(ex)}");
        }
        if (automaticCellsTask is not null)
        {
            try { automaticCells = (await automaticCellsTask).Where(cell => cell.ItemId == itemId).ToList(); }
            catch (Exception ex) when (ex is HttpRequestException or WcsApiException or TaskCanceledException)
            {
                // Row 37 of the queue: the rule of row 29 for this note too - it is a read of the wcs.db.
                notice = DrawioSaid.Failed(
                    $"The automatic cells of this device could not be read because {DrawioFailureWords.Why(ex)}: the line about them is missing, the rest of this panel is not.", ex);
            }
        }

        if (generation != _generation) return;
        _cellsMappings = mappings;
        _cellsWmsCells = cells;
        _cellsAutomaticCells = automaticCells;
        _cellsNotice = notice;
        _cellsError = failures.Count == 0 ? null : CellsNotShown(string.Join("; ", failures));
        _cellsErrorDetail = details.Count == 0 ? null : string.Join("\n", details);
        Notify();
    }

    /// <summary>
    /// The sentence of the cells that are not shown (row 29 of the queue): ONE sentence - what failed,
    /// each with its why, and that nothing was changed - in which the reads that failed are separated
    /// by semicolons, so that each why stays with its own read.
    /// </summary>
    private static string CellsNotShown(string what) =>
        $"The cells of this device are not shown: {what}; nothing here has been changed.";

    /// <summary>
    /// Why a device written by the panel could not be read back (row 37 of the queue): the commands
    /// read it with <c>GetItemAsync</c> and throw <see cref="InvalidOperationException"/> - in their own
    /// words, behind the "?" - only when the host answers that it does not have it
    /// (<c>EditItemConfigCommand</c>, <c>LinkBufferCellCommand</c>, <c>UnlinkBufferCellCommand</c>);
    /// anything else is a failure of the read like any other (<see cref="DrawioFailureWords.Why"/>).
    /// </summary>
    private static string WhyNotReadBack(Exception ex) => ex is InvalidOperationException
        ? "the host does not have it any more"
        : DrawioFailureWords.Why(ex);

    /// <returns>
    /// Null when the drawing took the new name; otherwise what the operator must be told about the label
    /// left behind - in one sentence, with the drawing's own failure behind the line's "?" (row 37 of the
    /// queue), in the panel and in the device list alike.
    /// </returns>
    private async Task<DrawioSaid?> RenameConfigOnCanvasAsync(Guid itemId, string? name)
    {
        // alreadyOnThePlant: true - this runs after the config write to the plant has gone through
        // (the sentences below say "The new name is on the plant, but..."), so the model edit below is
        // an echo and must not light the "changed since last save" mark.
        var outcome = await renameCell(DrawnCellOf(itemId), name, true);
        if (outcome.Renamed) return null;
        if (!outcome.Available)
        {
            return new DrawioSaid(
                "The new name is on the plant, but the drawing could not be told, so it still shows the old label until the page is reloaded.",
                outcome.Failure is { Length: > 0 } failure ? failure : null);
        }

        return "The new name is on the plant, but the drawing did not take it (the cell is no longer there, or the selection could not be followed): the canvas still shows the old label until the page is reloaded.";
    }

    private async Task<bool> WriteCellsAsync(Guid subject, string gesture, EditCommand command)
    {
        _cellsWriteError = null;
        _cellsWritten = null;
        if (_configItemId != subject || graph() is not { } current)
        {
            _cellsWriteError = $"This page is no longer showing that device's cells (the selection moved on while the confirmation was on its way): {gesture} wrote nothing. Select the device again and try once more.";
            return false;
        }

        if (!writeGate.TryEnter(DrawioPlantWriter.Cells, out var writeLease))
        {
            _cellsWriteError = DrawioWriteRefusal.ForCells(writeGate.Current, gesture);
            return false;
        }

        using var cellsLease = writeLease!;
        try
        {
            if (await ensureWritable() is { } refused)
            {
                _cellsWriteError = refused;
                return false;
            }
            await command.ApplyAsync(session.Client, current);
            return true;
        }
        catch (WcsApiException gone) when (gone.StatusCode == HttpStatusCode.NotFound)
        {
            _cellsWriteError = $"{gesture} wrote NOTHING: the host does not have this device any more - somebody deleted it while this panel was open. No row was left behind and the device was not brought back to life. Reload the page.";
            return false;
        }
        catch (InvalidOperationException ex)
        {
            _cellsWriteError = DrawioSaid.Failed(
                $"The cells were written, but the device could not be read back afterwards because {WhyNotReadBack(ex)}: reload the page before changing anything else.", ex);
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or WcsApiException or TaskCanceledException)
        {
            // Final review, M9: a refusal the host's answer makes certain wrote nothing of THAT request,
            // but a cells command makes several - the configuration, the door map, the reading back - and
            // the ones before it went through. Said as that, and not as a refusal that "may have reached
            // the host anyway".
            _cellsWriteError = DrawioSaid.Failed(DrawioFailureWords.RefusalMeaning(ex) is { } refused
                ? $"{gesture} stopped because the host refused one of its requests - {refused}: that request wrote nothing, " +
                  "but the ones before it, if there were any, went through, so check the device before trying again."
                : $"{gesture} failed because {DrawioFailureWords.WhyAWriteFailed(ex)}, and whether it went through is NOT known - " +
                  "the request may have reached the host anyway, so check the device before trying again.", ex);
            return false;
        }
        finally
        {
            Notify();
        }
    }

    private async Task SaveDoorMappingsCoreAsync(IReadOnlyList<DoorMappingDto> wanted)
    {
        if (_configItemId is not { } itemId || _cellsMappings is not { } before || wanted.Any(mapping => mapping.ItemId != itemId))
        {
            _cellsWriteError = "This page is no longer showing that device's door map (the selection moved on while the confirmation was on its way): nothing was written. Select the device again and confirm once more.";
            Notify();
            return;
        }
        if (!await WriteCellsAsync(itemId, "the door map", new ReplaceDoorMappingsCommand(itemId, before, wanted))) return;
        _cellsWritten = $"Written: the door map of {ConfigSubjectName} is on the plant ({DescribeDoorMappings(wanted)}).";
        await RereadDoorMappingsAsync(itemId);
        Notify();
    }

    private async Task ClearDoorMappingsCoreAsync()
    {
        if (_configItemId is not { } itemId || _cellsMappings is not { } before)
        {
            _cellsWriteError = "This page is no longer showing that device's door map (the selection moved on while the gesture was on its way): nothing was written. Select the device again and try once more.";
            Notify();
            return;
        }
        if (!await WriteCellsAsync(itemId, "unmapping the doors", new ClearDoorMappingsCommand(itemId, before))) return;
        _cellsWritten = $"Written: {ConfigSubjectName} has no doors mapped on the plant any more.";
        await RereadDoorMappingsAsync(itemId);
        Notify();
    }

    private async Task LinkCellCoreAsync(WmsCell cell)
    {
        if (_configItemId is not { } itemId || _selectedConfig is not { } oldConfig ||
            _cellsMappings is not { } before || oldConfig.ItemId != itemId)
        {
            _cellsWriteError = "This page is no longer showing that device's cell (the selection moved on while the gesture was on its way): nothing was written. Select the device again and try once more.";
            Notify();
            return;
        }
        if (cell.DoorA is not { } doorCode)
        {
            _cellsWriteError = $"Cell {Cell(cell.CellId)} has no door A: there is no door code to connect it by, and nothing was written.";
            Notify();
            return;
        }
        var newConfig = CellLinking.WithLinkedCellId(oldConfig, cell.CellId);
        var command = new LinkBufferCellCommand(oldConfig, newConfig, before, new DoorMappingLink(itemId, doorCode, cell.CellId));
        if (!await WriteCellsAsync(itemId, "connecting the cell", command)) return;
        _cellsWritten = $"Written: {ConfigSubjectName} is connected to cell {Cell(cell.CellId)} on the plant. The properties below have been read again from the host, so anything typed into them and not confirmed is gone.";
        await RereadDoorMappingsAsync(itemId);
        await RereadAfterCellLinkAsync(itemId);
        Notify();
    }

    private async Task UnlinkCellCoreAsync()
    {
        if (_configItemId is not { } itemId || _selectedConfig is not { } oldConfig ||
            _cellsMappings is not { } before || oldConfig.ItemId != itemId)
        {
            _cellsWriteError = "This page is no longer showing that device's cell (the selection moved on while the gesture was on its way): nothing was written. Select the device again and try once more.";
            Notify();
            return;
        }
        var newConfig = CellLinking.WithLinkedCellId(oldConfig, 0m);
        if (!await WriteCellsAsync(itemId, "disconnecting the cell", new UnlinkBufferCellCommand(oldConfig, newConfig, before))) return;
        _cellsWritten = $"Written: {ConfigSubjectName} is no longer connected to any cell on the plant. The properties below have been read again from the host, so anything typed into them and not confirmed is gone.";
        await RereadDoorMappingsAsync(itemId);
        await RereadAfterCellLinkAsync(itemId);
        Notify();
    }

    private async Task RereadDoorMappingsAsync(Guid itemId)
    {
        try
        {
            var mappings = (await session.Client.GetDoorMappingsAsync()).Where(mapping => mapping.ItemId == itemId).ToList();
            if (_configItemId != itemId) return;
            _cellsMappings = mappings;
        }
        catch (Exception ex)
        {
            if (_configItemId != itemId) return;
            _cellsNotice = DrawioSaid.Failed(
                $"The door map on screen could not be read back from the host after the write because {DrawioFailureWords.Why(ex)}, " +
                "so what is above is how it was BEFORE: select the device again to see what the plant now holds.", ex);
        }
    }

    private async Task RereadAfterCellLinkAsync(Guid itemId)
    {
        try
        {
            var config = await session.Client.GetItemConfigurationAsync(itemId);
            var automaticCells = (await session.Client.GetAutomaticCellsAsync()).Where(cell => cell.ItemId == itemId).ToList();
            if (_configItemId != itemId) return;
            if (config is null)
            {
                _selectedConfig = null;
                _configError = $"The configuration could not be read: {itemId} is not (any more) on the host.";
                ForgetSelectedCellsPanel();
                return;
            }
            var kindBefore = SelectedCellsKind;
            _selectedConfig = config;
            _cellsAutomaticCells = automaticCells;
            if (SelectedCellsKind != kindBefore)
            {
                ForgetSelectedCellsPanel();
                _cellsError = "This device is no longer the kind it was when its cells were read (another editor changed its type meanwhile): what was on screen has been taken away rather than redrawn from lists that belong to the other kind. Select the device again to read its cells afresh.";
            }
        }
        catch (Exception ex)
        {
            if (_configItemId != itemId) return;
            _cellsNotice = DrawioSaid.Failed(
                $"The configuration could not be read back from the host after the write because {DrawioFailureWords.Why(ex)}, " +
                "so the fields below still show the cell this device had BEFORE: select the device again before confirming them, or they would write that old cell back.", ex);
        }
    }

    private static string DescribeDoorMappings(IReadOnlyList<DoorMappingDto> mappings) => mappings.Count switch
    {
        0 => "none mapped yet.",
        1 => "1 door mapped.",
        _ => $"{mappings.Count} doors mapped.",
    };

    private static string Cell(decimal cellId) =>
        cellId.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private static string NameOf(PlantItemDto item) =>
        item.Name is { Length: > 0 } name ? name : item.ItemId.ToString();

    private void Notify() => Changed?.Invoke();

    /// <summary>
    /// What a 409 or a 423 means on a write of the plant, and nothing of the host's words: every line
    /// of the page puts those behind its "?" with the rest of the answer (row 37 of the queue, extended
    /// to the whole page by the owner's answer of 10/10). Until then a sibling of this one,
    /// <c>HostRefusalReason</c>, quoted them in the sentence ("(the host said: ...)"); with no line left
    /// that quotes them it went, with the reading of the host's title and detail it used.
    /// </summary>
    /// <remarks>
    /// Final review: a 409 is not "the engine is in Run" any more. The host's convention is that a
    /// 409 is a well-formed request the plant's state forbids NOW (<c>EngineExceptionHandler</c>),
    /// and it has several: the engine not stopped - titled "Il motore non è fermo", the state in the
    /// detail, and not only Run -, a PLC connection still in use, work pending. So the page says only
    /// what a 409 means, and the host's words behind the "?" say which one it was.
    /// </remarks>
    internal static string HostRefusalMeaning(WcsApiException refused) => refused.StatusCode == HttpStatusCode.Locked
        ? "the edit permit is not (any longer) this session's"
        : "the plant's state does not allow it now";


    /// <summary>
    /// Which of the host's two 503s an answer is (row 34 of the queue): the field the host writes for
    /// it (<see cref="ServiceUnavailableProblems.ReasonExtension"/>) - <see cref="ServiceUnavailableProblems.EngineNotLoaded"/>
    /// or <see cref="ServiceUnavailableProblems.WarehouseUnreachable"/> - read as it came, never guessed
    /// from the title or the status. Null for a body without it, or that is not a problem document.
    /// </summary>
    internal static string? UnavailableReason(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var problem = JsonDocument.Parse(body);
            return problem.RootElement.ValueKind == JsonValueKind.Object
                   && problem.RootElement.TryGetProperty(ServiceUnavailableProblems.ReasonExtension, out var reason)
                   && reason.ValueKind == JsonValueKind.String
                ? reason.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
