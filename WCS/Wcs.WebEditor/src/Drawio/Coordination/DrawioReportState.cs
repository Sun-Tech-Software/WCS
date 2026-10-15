namespace Wcs.WebEditor.Drawio.Coordination;

/// <summary>
/// A device the drop created: its line, whether the drawing took its name, and - when the drawing
/// could not be told - what the page's script threw, behind the line's "?" (row 37 of the queue,
/// extended to the whole page on 10/10).
/// </summary>
public readonly record struct CreationLine(string Text, bool CanvasFollowed, string? Detail = null);

public sealed record DrawioCreationReportState(
    string? Error,
    IReadOnlyList<CreationLine> Created,
    IReadOnlyList<DrawioSaid> NotCreated,
    bool Unavailable,
    string UnavailableMessage,
    bool Creating)
{
    /// <summary>
    /// What came back for an <see cref="Error"/> about a request that failed - the edit permit that
    /// could not be asked for - behind the "?" of the error's line (row 37 of the queue); null otherwise.
    /// </summary>
    public string? ErrorDetail { get; init; }

    /// <summary>
    /// How many of <see cref="NotCreated"/> are shapes whose device may be on the plant all the same:
    /// the request got no answer, or failed in a way that may have let part of it through. Their lines
    /// say so, and say to look before dragging the shape again - a shape surely not created and one
    /// NOT KNOWN to be are told apart where the report is summed up (DrawioCreationReport).
    /// </summary>
    public int NotKnown { get; init; }
}

public sealed record DrawioSaveReportState(
    string? Error,
    string? Summary,
    IReadOnlyList<string> Written,
    IReadOnlyList<string> Unconfirmed,
    IReadOnlyList<string> NotWritten,
    IReadOnlyList<DrawioRefusal> Refusals,
    IReadOnlyList<DrawioNotice> Notices)
{
    /// <summary>
    /// What came back for an <see cref="Error"/> about a request that failed - the edit permit that
    /// could not be asked for - behind the "?" of the error's line (row 37 of the queue); null otherwise.
    /// </summary>
    public string? ErrorDetail { get; init; }
}
