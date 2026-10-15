using Microsoft.AspNetCore.Components;

namespace Wcs.WebEditor.Drawio.Components;

/// <summary>The colour of an outcome in the row under the bar: green done, orange a warning, red a refusal or a failure.</summary>
public enum DrawioNoticeTone
{
    Ok,
    Warn,
    Bad,
}

/// <summary>
/// One outcome of the row under the bar (<see cref="DrawioNotices"/>, passata grafica 2026-09-29): one
/// sentence in its tone, "Details" when there is a whole report behind it, and an X when it is
/// transient - an outcome that lasts as long as its condition (the drawing out of reach, the plant's
/// devices that are not drawn) has none.
/// </summary>
/// <remarks>
/// Not <c>DrawioNotice</c>, the name the plan gave it: that is already the save report's notice
/// (<see cref="Wcs.WebEditor.Drawio.DrawioNotice"/>), and a file that imports both namespaces - the
/// page does - could not tell the two apart.
/// </remarks>
/// <param name="Id">
/// The outcome's name, unique on the page: its line is <c>{Id}-notice</c>, its "Details" button
/// <c>{Id}-details-toggle</c>, its window <c>{Id}-details</c> and its X <c>{Id}-dismiss</c>.
/// </param>
/// <param name="Line">The one sentence the row shows.</param>
/// <param name="Dismissable">Whether the X closes it: transient outcomes only.</param>
/// <param name="Details">The whole report, as it was on the page before the row existed, in a window; null for a line that is all there is to say.</param>
public sealed record DrawioNoticeLine(string Id, DrawioNoticeTone Tone, string Line, bool Dismissable, RenderFragment? Details)
{
    /// <summary>The title of the window "Details" opens.</summary>
    public string DetailsTitle { get; init; } = "Details";

    /// <summary>
    /// What the report is about, behind the "?" of its window's title bar (DrawioWindow.Help), as every
    /// window of the page has it; null for no "?".
    /// </summary>
    public RenderFragment? Help { get; init; }

    /// <summary>
    /// Which outcome of this kind it is - the save's round, say. The same number is the same outcome:
    /// it keeps its place in the row and, once its X was pressed, stays closed. A new number is a new
    /// outcome, even with the same words: it goes on top and shows again.
    /// </summary>
    public long Occurrence { get; init; }

    /// <summary>
    /// The id the sentence had on the page before the row existed (<c>#drawio-cut-refused</c>): the
    /// tests and the bridge's helpers look for it there, so the sentence keeps it.
    /// </summary>
    public string? LineId { get; init; }

    /// <summary>The class the sentence had before the row existed (<c>drawio-bridge-error</c>), for the same reason.</summary>
    public string? LineClass { get; init; }

    /// <summary>The role the sentence had before the row existed (<c>status</c>: the refused cut is announced as it comes).</summary>
    public string? LineRole { get; init; }

    /// <summary>
    /// What came back for a line about a failure - the host's answer as it came, or the failure's type
    /// and message - behind a "?" right after the sentence (row 37 of the queue, extended to the whole
    /// page on 10/10): the "?" is <c>{sentence id}-help</c> and the text <c>{sentence id}-detail</c>,
    /// as on every failure line of the page (DrawioFailureLine). Null for a line that says no failure.
    /// </summary>
    public string? LineDetail { get; init; }
}
