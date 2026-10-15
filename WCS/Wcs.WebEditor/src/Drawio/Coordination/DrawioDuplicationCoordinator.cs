using System.Globalization;
using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Drawing;
using Wcs.WebEditor.Editing;
using Wcs.WebEditor.Model;
using Wcs.WebEditor.Session;

namespace Wcs.WebEditor.Drawio.Coordination;

/// <summary>
/// One line of the duplication's report: what it says, whether it is the good news or not, and what
/// came back for a failure it says - behind the line's "?" (row 37 of the queue), null when none.
/// </summary>
public sealed record DrawioDuplicationLine(string Text, bool WentThrough, string? Detail = null);

/// <summary>
/// A device's cell as the drawing holds it NOW (drawio-bridge.js, cellBox): its own box - not the
/// plant's rotated envelope - and the rotation its style carries.
/// </summary>
public sealed record DrawioCellBox(double X, double Y, double Width, double Height, double Rotation);

/// <summary>What became of the duplicate's cell on the drawing (fix round 4: four answers, not two).</summary>
public enum DrawioDrawState
{
    /// <summary>On the canvas, and selected.</summary>
    Drawn,

    /// <summary>On the canvas - the cell was added - but not selected (or it is not known whether it was).</summary>
    DrawnNotSelected,

    /// <summary>Not on the canvas.</summary>
    NotDrawn,

    /// <summary>Nobody can say: the answer was lost, and the drawing could not be asked either.</summary>
    Unknown,
}

/// <summary>
/// What became of the duplicate's cell, why in words, and what came back from the drawing when its
/// answer was a failure - its type and message, behind the report line's "?" (row 37 of the queue).
/// </summary>
public sealed record DrawioDrawOutcome(DrawioDrawState State, string? Why = null, string? Detail = null);

/// <summary>
/// Row 63 (compito 5, fix round 2, owner's decision): the plant-level duplicate of a device - the
/// WinForms "Duplicate Item" (<c>FrmEDITOR.Private.cs:938</c> -> <c>DiagramManager.DuplicateAutomationDevice</c>,
/// <c>DiagramManager.cs:446-468</c>). A new device of the same type, with the source's whole
/// configuration copied, drawn next to it.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is copied, and what is not.</b> The copy is <see cref="DuplicateItemCommand.CopyConfigWithNewId"/>,
/// the web's existing twin of the WinForms <c>ConfigCopier.Copy</c> (<c>Wcs.Editor/src/Model/ConfigCopier.cs:10-12</c>):
/// every writable property of <see cref="PlantItemConfigDto"/> but the identifier, and the ASI set
/// to <c>"XXXX"</c> - exactly the two the WinForms resets. Then <see cref="ResetOnTheDuplicate"/>
/// resets what THIS page resets on top of that, one line each, both fields the WinForms copied: the
/// NAME and the LINKED WAREHOUSE CELL - see that method for why. Everything else - the links to
/// other devices (build destinations, reject/relocate devices, triggers), the PLC connection, the
/// group fields - is copied, as the WinForms copies it. Not copied, as in the WinForms: the junctions
/// (a node's clone carries no connection) and the door mappings of a Storage device, which are not
/// configuration.
/// </para>
/// <para>
/// <b>One write carries the configuration.</b> The WinForms created the device empty and then saved
/// the copied configuration over it - two writes, and a device with the default configuration when
/// the second failed. <c>POST /plant/items</c> stores the whole configuration it is given
/// (<c>PlantEndpoints.cs:102-109</c>), so here the device is born with the copy or not at all. What can
/// still be left half-done is the POSITION, written next (<c>PUT .../geometry</c>), and the drawing:
/// both are said, never passed over.
/// </para>
/// <para>
/// <b>Where it goes.</b> 100 to the right and 100 below the source AS IT IS DRAWN NOW - the WinForms
/// moved the live node's pin point, so a source moved and not yet saved is duplicated where the
/// operator sees it (fix round 3). The source's cell is asked of the drawing (cellBox) and turned into
/// the plant's box by <see cref="NodePlacement.EnvelopeBox"/>, the save's own conversion; when the
/// drawing cannot tell, the plant's position is used and the report says so. It is drawn by the bridge with <see cref="DrawioDocument.DeviceCellXml"/>
/// - the very cell the next opening draws for it - and selected. That insertion is a model edit like
/// a drop, so the "Leave site?" mark goes on as it does after a device dropped and created; the
/// creation of the page does not take it for a shape to create, because its identifier is already a
/// device of the plant (<c>DrawioCreationCoordinator.WantsADevice</c>).
/// </para>
/// <para>
/// <b>A write like the others.</b> Through the write gate with its own writer
/// (<see cref="DrawioPlantWriter.Duplicating"/>) and the edit permit taken at that moment
/// (<see cref="DrawioPlantWriteGate.WriteThroughAsync"/>): refused with the engine running, as the
/// creation of a device is.
/// </para>
/// </remarks>
internal sealed class DrawioDuplicationCoordinator(
    EditorSessionState session,
    DrawioPlantWriteGate writeGate,
    Func<PlantGraph?> graph,
    Func<Task<DrawioSaid?>> ensureWritable,
    Func<Guid, Task<(DrawioCellBox? Box, string? Why, string? Detail)>> sourceOnCanvas,
    Func<PlantItemDto, Task<DrawioDrawOutcome>> drawOnCanvas)
{
    /// <summary>How far from the source the duplicate is put, in plant units, to the right and below: the WinForms clone's pin point moved by 100 and 100.</summary>
    public const double Offset = 100;

    /// <summary>The ASI a duplicate is born with - <see cref="DuplicateItemCommand.CopyConfigWithNewId"/>'s, and the WinForms ConfigCopier's.</summary>
    public const string ResetAsi = "XXXX";

    /// <summary>The "no cell" value of <see cref="PlantItemConfigDto.LinkedCellId"/>: the one the panel reads as "none connected".</summary>
    public const decimal NoLinkedCell = 0m;

    /// <summary>
    /// What this page resets on the copy ON TOP of the WinForms rule (ItemId, ASI), one line per
    /// field. Both are fields the WinForms <c>ConfigCopier.Copy</c> copied (<c>ConfigCopier.cs:10-12</c>
    /// skips only the identifier):
    /// <list type="bullet">
    /// <item><b>The name</b> becomes the one the question offered (the suggested name every device this
    /// page creates gets, owner's decision of 2026-09-17): two devices with one name would be two
    /// indistinguishable rows in the lists, the checks and the "go to" of this page.</item>
    /// <item><b>The linked warehouse cell</b> (owner's decision, fix round 3): two devices would share one
    /// cell both as the buffer a mission goes to (<c>DBInfo.Private.cs:346-359</c>, BufferCellId from
    /// the destination's LinkedCellId) and as the cell a load unit is allocated to
    /// (<c>PathMission.Methods.cs:418</c>), and the copy would carry the id without the warehouse-side
    /// link that "Link" makes (<c>LinkedCells.razor:100</c>). It is linked from the panel.</item>
    /// </list>
    /// </summary>
    internal static void ResetOnTheDuplicate(PlantItemConfigDto copy, string name)
    {
        copy.Name = name;
        copy.LinkedCellId = NoLinkedCell;
    }

    private readonly List<DrawioDuplicationLine> _report = [];

    /// <summary>
    /// Devices this page created on the host and then took back out of its plant because they could
    /// not be drawn (fix round 3): not shown until a reload, and yet their names are TAKEN. Consulted
    /// by the name the question offers and by the check at write time (fix round 4), so a second
    /// duplicate is not offered - or written under - the name of one the page cannot show.
    /// </summary>
    private readonly List<PlantItemDto> _createdNotShown = [];

    public event Action? Changed;

    public IReadOnlyList<DrawioDuplicationLine> Report => _report;

    public bool Duplicating => writeGate.Current is DrawioPlantWriter.Duplicating;

    /// <summary>The name the duplicate of <paramref name="sourceId"/> would be born with now, or null when the page cannot tell (no plant, no such device, a type the registry does not have).</summary>
    public string? NameForTheDuplicateOf(Guid sourceId) =>
        graph() is { } current
        && current.Items.FirstOrDefault(item => item.ItemId == sourceId) is { } source
        && current.TypesByDeviceType.TryGetValue(source.DeviceType, out var type)
            ? SuggestedDeviceName.For(type.Label, current.Items.Concat(_createdNotShown))
            : null;

    /// <summary>
    /// Duplicates <paramref name="sourceId"/> under the name the question offered,
    /// <paramref name="offeredName"/> - carried from the question to the write, not computed again:
    /// the operator said yes to THAT name. If another device took it meanwhile, nothing is written.
    /// </summary>
    public async Task DuplicateAsync(Guid sourceId, string? offeredName)
    {
        _report.Clear();
        Notify();
        try
        {
            if (await writeGate.WriteThroughAsync(DrawioPlantWriter.Duplicating, DrawioWriteRefusal.ForDuplication,
                    ensureWritable, () => DuplicateUnderTheGateAsync(sourceId, offeredName)) is { } refused)
                Say($"NOT duplicated: {refused.Text}", false, refused.Detail);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Say("The duplication stopped on a failure this page does not know how to tell apart: look at the plant before duplicating again.", false,
                DrawioFailureWords.Detail(ex));
        }
        finally
        {
            Notify();
        }
    }

    private async Task DuplicateUnderTheGateAsync(Guid sourceId, string? offeredName)
    {
        if (graph() is not { } current)
        {
            Say("NOT duplicated: the plant is not loaded yet. Nothing was created.", false);
            return;
        }

        if (current.Items.FirstOrDefault(item => item.ItemId == sourceId) is not { } source)
        {
            Say($"NOT duplicated: the page no longer has the device {sourceId}. Nothing was created.", false);
            return;
        }

        var sourceName = NameOf(source);
        if (!current.TypesByDeviceType.TryGetValue(source.DeviceType, out var type))
        {
            Say($"NOT duplicated: the type of {sourceName} ({source.DeviceType}) is not in the registry the engine declares, so a device of it could not be drawn. Nothing was created.", false);
            return;
        }

        if (offeredName is not { Length: > 0 } name)
        {
            Say($"NOT duplicated: the question did not offer a name for the new device (the plant or its type registry was not loaded then). Nothing was created - ask again.", false);
            return;
        }

        if (current.Items.Concat(_createdNotShown).Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            Say($"NOT duplicated: the name \"{name}\" the question offered is no longer free - another device has taken it since. Nothing was created: ask again, and the question offers a free name.", false);
            return;
        }

        var (drawn, notDrawnBecause, notDrawnDetail) = await ReadTheSourceOnTheCanvasAsync(sourceId);
        if (drawn is null && !PlantGraph.IsPlaced(source))
        {
            Say($"NOT duplicated: {sourceName} has no position - neither on the drawing ({notDrawnBecause}) nor on the plant - so there is nowhere to put its duplicate. Nothing was created.", false,
                notDrawnDetail);
            return;
        }

        PlantItemConfigDto? stored;
        try
        {
            // Read now and not taken from the panel: what is copied is what the plant holds at the
            // moment of the gesture, not what somebody may be typing in the panel.
            stored = await session.Client.GetItemConfigurationAsync(sourceId);
        }
        catch (Exception ex) when (ex is HttpRequestException or WcsApiException or TaskCanceledException)
        {
            Say($"NOT duplicated: the configuration of {sourceName} could not be read from the host because {DrawioFailureWords.Why(ex)}, so nothing was created.", false,
                DrawioFailureWords.Detail(ex));
            return;
        }

        if (stored is null)
        {
            Say($"NOT duplicated: the host no longer has {sourceName} (deleted elsewhere?). Nothing was created.", false);
            return;
        }

        var newId = Guid.NewGuid();
        var config = DuplicateItemCommand.CopyConfigWithNewId(stored, newId);
        ResetOnTheDuplicate(config, name);
        PlantItemGeometryDto geometry;
        string from;
        // What the drawing answered when it could not say where the source is: behind the "?" of the
        // line that says where the duplicate went instead (row 37 of the queue).
        string? fromDetail = null;
        // The drawn box goes through the SAVE's own rule (DrawnGeometry.Judge, fix round 4): an
        // unchanged source is the plant's geometry exactly, a moved one keeps the plant's size and
        // rotation, and where the save would refuse the drawn box (45 degrees) so does this.
        var verdict = drawn is { } box ? DrawnGeometry.Judge(source, box.X, box.Y, box.Width, box.Height, box.Rotation) : (DrawnGeometryVerdict?)null;
        if (verdict is { Geometry: { } judged })
        {
            geometry = new PlantItemGeometryDto(newId, (judged.X ?? 0) + Offset, (judged.Y ?? 0) + Offset,
                judged.Width ?? 0, judged.Height ?? 0, judged.Rotation ?? 0);
            from = $"where {sourceName} is drawn now";
        }
        else if (!PlantGraph.IsPlaced(source))
        {
            Say($"NOT duplicated: where {sourceName} is drawn cannot be written as a plant position ({RefusalWords(verdict?.Refusal)}), and the plant has no position for it either. Nothing was created.", false);
            return;
        }
        else
        {
            geometry = new PlantItemGeometryDto(newId, source.X!.Value + Offset, source.Y!.Value + Offset,
                source.Width ?? 0, source.Height ?? 0, source.Rotation ?? 0);
            from = verdict?.Refusal is { } refusal
                ? $"where the plant has {sourceName} - where it is drawn now cannot be written as a plant position ({RefusalWords(refusal)}), and the save would refuse it too"
                : $"where the plant has {sourceName} - the drawing could not say where it is drawn ({notDrawnBecause})";
            if (verdict?.Refusal is null) fromDetail = notDrawnDetail;
        }

        var copied = $"Configuration copied from {sourceName}: every field but the identifier, the name (\"{name}\"), the ASI (\"{stored.Asi}\" -> \"{ResetAsi}\") and the linked warehouse cell ({Cell(stored.LinkedCellId)} -> none: link it from the panel if needed); its junctions and warehouse door mappings are not copied.";

        try
        {
            await new DuplicateItemCommand(config, geometry, sourceName).ApplyAsync(session.Client, current);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await SayWhatTheFailureLeftAsync(ex, current, newId, name, sourceName, copied);
            return;
        }

        Say($"Created \"{name}\" ({newId}), a duplicate of {sourceName}, at ({N(geometry.X ?? 0)}, {N(geometry.Y ?? 0)}) - {N(Offset)} to the right of and {N(Offset)} below {from}.", true,
            fromDetail);
        Say(copied, true);
        await DrawAsync(current, newId, name);
    }

    /// <summary>
    /// The create or the position failed: which of the two is asked of the HOST, not guessed from the
    /// exception - a transport failure can come after the device was written.
    /// </summary>
    private async Task SayWhatTheFailureLeftAsync(Exception failure, PlantGraph current, Guid newId, string name, string sourceName, string copied)
    {
        PlantItemDto? there;
        try
        {
            there = await session.Client.GetItemAsync(newId);
        }
        catch (Exception ex) when (ex is HttpRequestException or WcsApiException or TaskCanceledException)
        {
            Say($"It is NOT known whether the duplicate of {sourceName} was created - the writing failed because {WhyItFailed(failure)}, and the host could not be asked afterwards because {DrawioFailureWords.Why(ex)} -, " +
                $"and if it was, it is {newId}, named \"{name}\": look for it before duplicating again.", false,
                $"The duplicate: {DrawioFailureWords.Detail(failure)}\nThe question afterwards: {DrawioFailureWords.Detail(ex)}");
            return;
        }

        if (there is null)
        {
            Say($"NOT duplicated because {WhyItFailed(failure)}: nothing was created on the plant.", false, DrawioFailureWords.Detail(failure));
            return;
        }

        if (current.Items.All(item => item.ItemId != newId)) current.AddItem(there);
        if (!PlantGraph.IsPlaced(there))
        {
            Say($"PARTLY duplicated: \"{name}\" ({newId}) exists on the plant, but its position was NOT written because {WhyItFailed(failure)}, so it is not drawn - it shows among the devices with no shape. {copied}", false,
                DrawioFailureWords.Detail(failure));
            return;
        }

        // Written after all - the failure came after the position landed (the reread of the new device).
        Say($"Created \"{name}\" ({newId}), a duplicate of {sourceName}, though the host's answer did not come back whole: {WhyItFailed(failure)}.", true,
            DrawioFailureWords.Detail(failure));
        Say(copied, true);
        await DrawAsync(current, newId, name);
    }

    private async Task DrawAsync(PlantGraph current, Guid newId, string name)
    {
        if (current.Items.FirstOrDefault(item => item.ItemId == newId) is not { } created)
        {
            Say($"\"{name}\" ({newId}) is on the plant, but the page does not hold it, so it is NOT drawn: reload the page to see it.", false);
            return;
        }

        DrawioDrawOutcome outcome;
        try
        {
            outcome = await drawOnCanvas(created);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            outcome = new DrawioDrawOutcome(DrawioDrawState.Unknown, "drawing it stopped on a failure this page does not know how to tell apart", DrawioFailureWords.Detail(ex));
        }

        switch (outcome.State)
        {
            case DrawioDrawState.Drawn:
                Say($"\"{name}\" is drawn on the canvas and selected; like a device dropped on the drawing, the drawing now holds work the next save writes.", true);
                return;
            case DrawioDrawState.DrawnNotSelected:
                Say($"\"{name}\" is drawn on the canvas, but it was not selected ({outcome.Why}): click it to see its properties. Like a device dropped on the drawing, the drawing now holds work the next save writes.", true,
                    outcome.Detail);
                return;
        }

        // OUT of the page's plant again (fix round 3): the save takes the plant the page holds as the
        // one it opened (DrawioSaveCoordinator), and a drawable device of that plant with no cell on
        // the drawing is a device the operator deleted - DrawioDelta would delete it at the next
        // save. Left out, the save ignores it, and the next opening draws it. Its name stays taken.
        current.RemoveItem(newId);
        _createdNotShown.Add(created);
        Say(outcome.State == DrawioDrawState.NotDrawn
            ? $"\"{name}\" ({newId}) is on the plant with its configuration and position, but it could NOT be drawn on the canvas ({outcome.Why}). It stays on the plant; this page shows it - on the drawing and in the lists - only after a reload, and until then the save leaves it alone."
            : $"\"{name}\" ({newId}) is on the plant with its configuration and position, but it is NOT known whether it was drawn on the canvas ({outcome.Why}). The save leaves it alone until a reload shows it; if its cell did reach the canvas, the save refuses that cell as a shape the plant does not know, and nothing of the device is lost. Reload the page.", false,
            outcome.Detail);
    }

    private async Task<(DrawioCellBox? Box, string? Why, string? Detail)> ReadTheSourceOnTheCanvasAsync(Guid sourceId)
    {
        try
        {
            return await sourceOnCanvas(sourceId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (null, "asking it stopped on a failure this page does not know how to tell apart", DrawioFailureWords.Detail(ex));
        }
    }

    private static string RefusalWords(DrawnGeometryRefusal? refusal) => refusal switch
    {
        DrawnGeometryRefusal.ResizedAtDegenerateRotation => "it was resized at a rotation of 45 degrees and not saved",
        DrawnGeometryRefusal.RotatedAwayFromDegenerateRotation => "it was turned away from a rotation of 45 degrees and not saved",
        DrawnGeometryRefusal.TurnedToDegenerateRotation => "it was turned to 45 degrees and not saved, which the plant cannot represent",
        _ => "the drawing could not say where it is",
    };

    private static string Cell(decimal cellId) =>
        cellId == NoLinkedCell ? "none" : cellId.ToString("0.##", CultureInfo.InvariantCulture);

    private void Say(string text, bool wentThrough, string? detail = null)
    {
        _report.Add(new DrawioDuplicationLine(text, wentThrough, detail));
        Notify();
    }

    private void Notify() => Changed?.Invoke();

    private static string NameOf(PlantItemDto item) =>
        item.Name is { Length: > 0 } name ? name : item.ItemId.ToString();

    private static string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// Why the create or the position failed, in the page's words (row 37 of the queue): a 409 here is
    /// the engine's state - Run, and since the owner's decisions of 27/9 not Stop - or an identifier
    /// already taken, and the host says which behind the "?" (confirmation review, M-E, had it in the
    /// sentence). <see cref="DuplicateItemCommand"/> and <c>GeometryWriter</c> throw
    /// <see cref="InvalidOperationException"/>, in their own words, only on an answer of the host's that
    /// does not fit what was written.
    /// </summary>
    private static string WhyItFailed(Exception ex) => ex is InvalidOperationException
        ? "the host's answer was not the one expected"
        : DrawioFailureWords.WhyAWriteFailed(ex);
}
