using System.Net;
using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Drawing;
using Wcs.WebEditor.Editing;
using Wcs.WebEditor.Model;
using Wcs.WebEditor.Session;

namespace Wcs.WebEditor.Drawio.Coordination;

internal sealed class DrawioCreationCoordinator(
    EditorSessionState session,
    DrawioPlantWriteGate writeGate,
    Func<PlantGraph?> graph,
    IDictionary<string, Guid> plantItemByDrawnCell,
    Func<Task<DrawioSaid?>> ensureWritable,
    Func<string, string, Task<DrawioSaid?>> renameOnCanvas,
    Func<Task> refreshSelection)
{
    private const string DeviceMark = "device";
    private const string UnavailableMessage =
        "A shape dropped on the drawing can no longer become a device: this page cannot hear draw.io's additions. " +
        "Drawing and saving still work, and what is already on the plant is untouched. Reload the page.";

    private readonly List<DrawioAddedCell> _pending = [];
    private readonly List<CreationLine> _created = [];
    private readonly List<DrawioSaid> _notCreated = [];
    private int _notKnown;
    private bool _draining;
    private bool _unavailable;
    private DrawioSaid? _error;

    public event Action? Changed;

    /// <summary>
    /// Which report <see cref="State"/> holds: one more every time a drop starts a report of its own. Two
    /// drops can end in the same words, and the second must show even when the first was closed: the
    /// row under the bar reads this number together with how much the report has said, which within
    /// one report only grows (DrawioCreationReport.NoticeOf).
    /// </summary>
    public long Report { get; private set; }

    public DrawioCreationReportState State => new(
        _error?.Text,
        _created,
        _notCreated,
        _unavailable,
        UnavailableMessage,
        writeGate.Current is DrawioPlantWriter.Creating)
    {
        NotKnown = _notKnown,
        ErrorDetail = _error?.Detail,
    };

    public async Task CellsAddedAsync(IReadOnlyList<DrawioAddedCell> cells)
    {
        _unavailable = false;
        if (cells.Count == 0)
        {
            Notify();
            return;
        }

        _pending.AddRange(cells);
        if (_draining) return;
        if (ConcernsUs()) ResetReport();

        _draining = true;
        try
        {
            do
            {
                await DrainAsync();
            }
            while (_pending.Count > 0);
        }
        finally
        {
            _draining = false;
        }

        if (_created.Count > 0) await refreshSelection();
        Notify();
    }

    public void MarkUnavailable()
    {
        _unavailable = true;
        Notify();
    }

    public void Reset()
    {
        ResetReport();
        _unavailable = false;
        Notify();
    }

    private bool WantsADevice(DrawioAddedCell cell, out string? reason)
    {
        reason = null;
        if (cell.Mark != DeviceMark) return false;

        if (string.IsNullOrEmpty(cell.Id))
        {
            reason = "A shape asking to become a device arrived WITHOUT AN IDENTIFIER of its own, which this page has no way to work with: it was NOT created. Everything that ties a device to its shape - the label, the rename from the panel, the save - is looked up by that identifier. Delete the shape and drag it again.";
            return false;
        }

        if (plantItemByDrawnCell.ContainsKey(cell.Id)) return false;

        if (Guid.TryParse(cell.Id, out var itemId))
        {
            if (graph()?.Items.Any(item => item.ItemId == itemId) == true) return false;
            reason = $"{cell.Id}: this cell already carries an identifier of the plant's own kind, but the plant does not know it (deleted by somebody else, or typed in by hand). It was NOT created: creating it would give it a different identifier, and everything that pointed at the old one would go on pointing at nothing.";
            return false;
        }

        return true;
    }

    private async Task DrainAsync()
    {
        var wanted = TakeWanted();
        if (wanted.Count == 0) return;

        if (graph() is not { } current)
        {
            _error = "The plant is not loaded yet: nothing was created. Delete the shape and drag it again in a moment.";
            RefuseAll(wanted, "the plant was not loaded");
            return;
        }

        if (!writeGate.TryEnter(DrawioPlantWriter.Creating, out var writeLease))
        {
            _error = DrawioWriteRefusal.ForCreation(writeGate.Current);
            RefuseAll(wanted, "another write to the plant was in progress");
            return;
        }

        using var creationLease = writeLease!;
        var served = 0;
        try
        {
            if (await ensureWritable() is { } refused)
            {
                _error = refused with { Text = $"Nothing was created. {refused.Text} The shapes are on the canvas and the plant is untouched: delete them and drag them again once this is sorted out." };
                RefuseAll(wanted, refused.Text);
                RefuseAll(TakeWanted(), refused.Text);
                return;
            }

            while (true)
            {
                for (; served < wanted.Count; served++)
                {
                    await CreateOneAsync(wanted[served], current);
                    Notify();
                }

                wanted = TakeWanted();
                served = 0;
                if (wanted.Count == 0) break;
            }
        }
        catch (Exception ex)
        {
            _error = DrawioWriteFailure.Said("The creation stopped", "", ex);
            RefuseAll(wanted.Skip(served), "the creation stopped on an unforeseen failure before this shape had its turn");
            RefuseAll(TakeWanted(), "the creation stopped on an unforeseen failure before this shape had its turn");
        }
    }

    private bool ConcernsUs()
    {
        foreach (var cell in _pending)
            if (WantsADevice(cell, out var reason) || reason is not null) return true;
        return false;
    }

    private void ResetReport()
    {
        _created.Clear();
        _notCreated.Clear();
        _notKnown = 0;
        _error = null;
        Report++;
    }

    private List<DrawioAddedCell> TakeWanted()
    {
        var wanted = new List<DrawioAddedCell>();
        foreach (var cell in _pending)
        {
            if (WantsADevice(cell, out var refused)) wanted.Add(cell);
            else if (refused is { } said) _notCreated.Add(new DrawioSaid(said));
        }

        _pending.Clear();
        return wanted;
    }

    private void RefuseAll(IEnumerable<DrawioAddedCell> cells, string reason) =>
        _notCreated.AddRange(cells.Select(cell => new DrawioSaid($"{cell.Id}: {reason}.")));

    private async Task CreateOneAsync(DrawioAddedCell cell, PlantGraph current)
    {
        if (cell.Parent != DrawioDocument.DefaultLayerId)
        {
            _notCreated.Add(new DrawioSaid($"{cell.Id}: the shape is not on layer 1, the only layer this Editor writes (parent=\"{cell.Parent ?? "(none)"}\"): it is either inside a group, where draw.io writes the position relative to the GROUP and the device would be born somewhere else entirely, or on a second layer, which the save would refuse to write for the rest of its life. It was NOT created - put the shape on layer 1, outside any group, and drag it again."));
            return;
        }

        if (cell.Width <= 0 || cell.Height <= 0)
        {
            _notCreated.Add(new DrawioSaid($"{cell.Id}: the shape arrived with no size ({cell.Width} x {cell.Height}): a device without a box is not drawn anywhere. It was not created."));
            return;
        }

        if (!short.TryParse(cell.Type, out var code)
            || current.Types.FirstOrDefault(candidate => candidate.NumericCode == code) is not { } type)
        {
            _notCreated.Add(new DrawioSaid($"{cell.Id}: the shape does not say which device type it is (it carries no valid \"{DrawioLibrary.TypeAttribute}\"{(cell.Type is { Length: > 0 } declared ? $", it carries \"{declared}\"" : "")}), and the type is not guessed from how it looks. It was not created - drag it out of the \"{DrawioLibrary.SectionTitle}\" section of the shapes panel."));
            return;
        }

        var newId = Guid.NewGuid();
        var name = SuggestedDeviceName.For(type.Label, current.Items);
        var (x, y, width, height) = NodePlacement.EnvelopeBox(cell.X, cell.Y, cell.Width, cell.Height, 0);
        var config = new PlantItemConfigDto { ItemId = newId, DeviceType = type.NumericCode, Name = name };
        var geometry = new PlantItemGeometryDto(newId, x, y, width, height, 0);

        try
        {
            await new CreateItemCommand(config, geometry, type.Label).ApplyAsync(session.Client, current);
        }
        catch (Exception ex) when (ex is OperationCanceledException
            || (ex is HttpRequestException transport && MayHaveBeenExecuted(transport)))
        {
            _notCreated.Add(DrawioSaid.Failed(
                $"{cell.Id}: the request got no answer because {DrawioFailureWords.Why(ex)}, and whether the device was created is NOT KNOWN - if it was, it is {newId}, named \"{name}\". Go and look before dragging the shape again, or you will end up with two.",
                ex));
            _notKnown++;
            return;
        }
        // The command's own findings (CreateItemCommand, GeometryWriter.ApplyConfirmation) are said in
        // its own words: the device not on the host when read back, or a geometry confirmation that does
        // not match - after which the device is on the host in full (final fix round, C3).
        catch (Exception ex) when (ex is HttpRequestException or WcsApiException or InvalidOperationException)
        {
            _notCreated.Add(DrawioWriteFailure.Said($"{cell.Id}: the device was not created, or not completely,",
                $": if any part of it got through it is {newId}, and the name it was being given is \"{name}\". Go and look for that identifier before dragging the shape again", ex));
            _notKnown++;
            return;
        }
        catch (Exception ex)
        {
            _notCreated.Add(DrawioWriteFailure.Said($"{cell.Id}: the creation stopped",
                $": if any part of it got through it is {newId}, and the name it was being given is \"{name}\". Go and look for that identifier before dragging the shape again", ex));
            _notKnown++;
            return;
        }

        plantItemByDrawnCell[cell.Id] = newId;
        _created.Add(new CreationLine($"{type.Label} ({newId}) at ({N(x)}, {N(y)}), named \"{name}\". The name is a SUGGESTION - the type's label plus the first free number - that nobody chose: change it in the panel next to the drawing.", true));
        if (await renameOnCanvas(cell.Id, name) is { } stale)
            _created[^1] = new CreationLine(_created[^1].Text + stale.Text, false, stale.Detail);
    }

    private void Notify() => Changed?.Invoke();

    private static string N(double value) =>
        value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private static bool MayHaveBeenExecuted(HttpRequestException transport) =>
        transport.HttpRequestError is not (HttpRequestError.NameResolutionError
            or HttpRequestError.ConnectionError
            or HttpRequestError.SecureConnectionError
            or HttpRequestError.ProxyTunnelError);
}
