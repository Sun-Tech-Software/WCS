namespace Wcs.WebEditor.Drawio;

/// <summary>
/// A device of this drawing that somebody else deleted from the plant while the page was open, with
/// the cells the page takes off the canvas for it at THIS save: its own cell and the arrows of its
/// junctions (the host takes a device's junctions away with it - <c>PlantStore.DeleteItemAsync</c>).
/// Owner's decision of 27/9, coda dei lavori voce 7: ONE save is enough.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the delta hands this over instead of saying it.</b> What the operator reads depends on
/// what the canvas ANSWERS, which the delta cannot know: a device taken off the drawing is news, a
/// device that could not be taken off is a job for the operator, and the report has to say which of
/// the two happened. So the delta names the cells and the page, after asking the bridge, prints
/// <see cref="Sentence"/> - one place for the words, called with the outcome.
/// </para>
/// <para>
/// <b>Before the decision</b> the cell stayed, and from the next save it was a marked cell the plant
/// did not know - an orphan every save refused until the operator deleted it by hand.
/// </para>
/// </remarks>
/// <param name="ItemId">The device, as the page was opened with it.</param>
/// <param name="Name">Its name, as the page was opened with it: the host no longer has one to give.</param>
/// <param name="DeviceCellIds">The cells the save recognizes as this device - its own identifier, or draw.io's for one dropped in this session.</param>
/// <param name="ArrowCellIds">The cells the save recognizes as the arrows of the junctions that hung from it, under either of their names.</param>
/// <param name="Connections">How many junctions hung from it when the page was opened: they went with it.</param>
/// <param name="ChangedInThisDrawing">The operator had moved, resized, rotated, mirrored or grouped its cell: that work cannot be written anywhere.</param>
/// <param name="ReweightedArrows">How many of its arrows the operator had given another priority: not written, the connection no longer exists.</param>
/// <param name="LooseLines">How many other lines hung from its cell and stay on the canvas, disconnected at that end: the operator's own lines, or a junction he re-hooked onto it.</param>
public sealed record DrawioDeviceGone(
    Guid ItemId,
    string Name,
    IReadOnlyList<string> DeviceCellIds,
    IReadOnlyList<string> ArrowCellIds,
    int Connections,
    bool ChangedInThisDrawing,
    int ReweightedArrows = 0,
    int LooseLines = 0)
{
    /// <summary>
    /// Whether the line is a REFUSAL - something left for the operator to do, so the round has not
    /// written everything and the "Leave site?" mark stays on - rather than a notice. One case only:
    /// the cells are still on the canvas, and the next save will refuse them until he deletes them.
    /// </summary>
    /// <remarks>
    /// The operator's own changes to the device (moved, rotated, a new priority on its arrow) are
    /// lost, and the line says so, but they are NOT a refusal (fix round, MINOR 1): nothing is left
    /// to lose or to do about them, and a refusal would keep "Leave site?" lit over a canvas that is
    /// the plant again.
    /// </remarks>
    public bool IsRefusal(bool removedFromTheDrawing) => !removedFromTheDrawing;

    /// <summary>The report's line for this device, once the canvas has said whether its cells went.</summary>
    public string Sentence(bool removedFromTheDrawing)
    {
        var said = $"{Name}: deleted on the host by somebody else while you were drawing";
        if (Connections > 0)
            said += Connections == 1
                ? ", and the connection hanging from it went with it"
                : $", and the {Connections} connections hanging from it went with it";

        var arrows = ArrowCellIds.Count switch
        {
            0 => null,
            1 => "its arrow",
            var n => $"its {n} arrows",
        };

        if (removedFromTheDrawing)
        {
            said += " - removed from the drawing too";
            if (arrows is not null) said += $", together with {arrows}";
            said += ".";
        }
        else
        {
            said += " - it could not be removed from the drawing: delete it by hand";
            if (arrows is not null) said += $", with {arrows}";
            said += ". Until then every save refuses its shape as a device the plant does not know.";
        }

        if (ChangedInThisDrawing)
            said += " What you changed on it in this drawing is lost, because the device no longer exists.";

        if (ReweightedArrows > 0)
            said += (ReweightedArrows, ArrowCellIds.Count) switch
            {
                (1, 1) => " The priority you changed on its arrow was not written: that connection no longer exists.",
                (1, _) => " The priority you changed on one of its arrows was not written: that connection no longer exists.",
                _ => $" The priority you changed on {ReweightedArrows} of its arrows was not written: those connections no longer exist.",
            };

        // Only once the device is off the canvas: until then those lines still hang from it.
        if (removedFromTheDrawing && LooseLines > 0)
            said += LooseLines == 1
                ? " 1 line of yours that hung from it stays on the canvas, loose at one end."
                : $" {LooseLines} lines of yours that hung from it stay on the canvas, loose at one end.";

        return said;
    }
}
