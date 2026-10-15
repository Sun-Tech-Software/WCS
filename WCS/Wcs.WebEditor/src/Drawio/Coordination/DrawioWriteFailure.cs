using System.Net;
using Wcs.Api.Client;

namespace Wcs.WebEditor.Drawio.Coordination;

/// <summary>
/// How a failed plant write of this page is said, one way for all of them: the save, the geometry,
/// the creation, the selection's writes and the page's own. It was five identical private copies, each
/// saying "the engine is in Run" for every 409 - false since the host refuses writes in Recover and
/// Dispose too, and refuses for other reasons (a PLC connection in use, work pending). A 409 or a 423
/// is said by what it means on the route (<see cref="DrawioSelectionCoordinator.HostRefusalMeaning"/>),
/// and the host's own words are behind the line's "?" with the rest of its answer.
/// </summary>
internal static class DrawioWriteFailure
{
    /// <summary>
    /// The line of a failed plant write: <paramref name="head"/>, why it failed in the page's words
    /// (<see cref="DrawioFailureWords.WhyAWriteFailed"/>), then <paramref name="tail"/> - one sentence,
    /// with what came back behind its "?" (row 37 of the queue, extended to the whole page by the
    /// owner's answer of 10/10). Until then the failure's message - the client's Italian framing with
    /// the problem document in it, or the host's own words after a 409 - sat in the middle of it.
    /// Two failures are the page's own findings, not a failed request, and are said as they are with
    /// nothing behind a "?": a geometry conflict, which names the devices, and the
    /// <see cref="InvalidOperationException"/> of the page's commands (<c>Editing</c>), whose message is
    /// the page's own sentence - a junction no longer on the host, a host's answer that does not confirm
    /// what was asked. Every branch puts its why in the same place, between the head and the tail, so a
    /// head written for "because" reads the same in all of them (final fix round, C4: a conflict used to
    /// print the head and the tail back to back, ",:" after the creation's head). A failure none of the
    /// page's words name is said as one, and not as "because an unforeseen failure stopped it".
    /// </summary>
    public static DrawioSaid Said(string head, string tail, Exception ex) => ex switch
    {
        GeometryConflictException conflict =>
            $"{head} because the geometry changed elsewhere for {string.Join(", ", conflict.ItemIds)}{tail}. " +
            "No automatic retry was made, because it could overwrite the newer position, size or rotation. " +
            "Reload the plant before saving again.",
        InvalidOperationException own => $"{head} because {AsAClause(own.Message)}{tail}.",
        _ when DrawioFailureWords.WhyAWriteFailed(ex) == DrawioFailureWords.Unforeseen =>
            DrawioSaid.Failed($"{head} on an unforeseen failure{tail}.", ex),
        _ => DrawioSaid.Failed($"{head} because {DrawioFailureWords.WhyAWriteFailed(ex)}{tail}.", ex),
    };

    /// <summary>
    /// The page's own sentence as the clause after "because": its first letter lowered, unless it opens
    /// an acronym, and its closing period dropped - the line ends with its own.
    /// </summary>
    private static string AsAClause(string sentence)
    {
        var clause = sentence.Trim().TrimEnd('.');
        return clause.Length > 1 && char.IsUpper(clause[0]) && !char.IsUpper(clause[1])
            ? char.ToLowerInvariant(clause[0]) + clause[1..]
            : clause;
    }

    /// <summary>
    /// The host's answer as it came, for whoever is after exactly what came back: its status and its
    /// body, trimmed, or the status alone when the body is empty. Nothing is parsed - a problem
    /// document, with its title, its detail and the trace id that finds the request in the host's log,
    /// is quoted whole. One way for the page: the Plant file window quotes it for an answer that is not
    /// a problem document (ImportExport), and the "?" of the cells that could not be read for every
    /// answer (DrawioSelectionCoordinator, row 29 of the queue). Row 29 had first written a private copy
    /// of the window's, and the two already differed by the period after "an empty body".
    /// </summary>
    public static string AsItCame(WcsApiException fromHost) => string.IsNullOrWhiteSpace(fromHost.Body)
        ? $"the host answered {(int)fromHost.StatusCode} with an empty body"
        : $"the host answered {(int)fromHost.StatusCode}: {fromHost.Body.Trim()}";
}
