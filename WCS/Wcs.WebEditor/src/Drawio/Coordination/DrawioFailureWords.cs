using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using Wcs.Api.Client;
using Microsoft.JSInterop;
using Wcs.Api.Contracts.Dtos;

namespace Wcs.WebEditor.Drawio.Coordination;

/// <summary>
/// How a line of the properties panel says a failure (row 37 of the queue, the rule of row 29 for the
/// whole panel): ONE sentence in the page's words, with the why in it (<see cref="Why"/>), and what
/// came back - the host's answer as it came, or the failure's own type and message - behind the "?"
/// next to it (<see cref="Detail"/>, shown by <c>DrawioFailureLine</c>). Until row 37 only the cells
/// that could not be read were said this way; the configuration's error, the notes and the outcomes
/// of the panel's writes still carried <c>ex.Message</c> in the middle of the sentence - the client's
/// "L'API del motore ha risposto 500: ..." with the problem document in it, in Italian, in red. The
/// lines the panel shares with other windows - the edit permit that could not be asked for
/// (<c>DrawioEditor.EnsureWritableAsync</c>, <see cref="DrawioPlantWriteGate.WriteThroughAsync"/>), a
/// command's outcome (<see cref="DrawioCommandFollower"/>), the label the drawing did not take after a
/// rename - are said this way in every window that shows them (the owner's answer of 9/10). Since the
/// owner's answer of 10/10 the rule is the whole page's: every line that showed <c>ex.Message</c> or
/// the host's words - the checks, the engine, the save and the creation with their reports, the device
/// list, Operations, the PLC connections, the missions, the plant file, the "go to" lines, the live
/// link - says the why with these words and keeps what came back behind its "?". Three kinds of line
/// keep words that are not the page's why in the sentence, on purpose (final fix round, C5): the
/// page's own findings, which are already its words - the <see cref="InvalidOperationException"/> of
/// its commands (<see cref="DrawioWriteFailure.Said"/>) and a step's answer that could not be read back
/// (<c>DrawioGeometryCoordinator.NotWrittenPartOf</c>); a command's outcome, which quotes the engine's
/// <c>ErrorDescription</c> (<see cref="DrawioCommandFollower"/>, <c>DrawioEngineCoordinator.ReasonOf</c>,
/// the owner's answer of 9/10); and the import's unknown outcome, said in a browser alert that has no
/// "?" (<c>ImportExport</c>), so what came back follows "Detail:" in its text.
/// </summary>
internal static class DrawioFailureWords
{
    /// <summary>The why of a failure none of the page's words name: <see cref="Why"/> and <see cref="WhyTheScriptDidNotAnswer"/> end on it.</summary>
    public const string Unforeseen = "an unforeseen failure stopped it";

    /// <summary>
    /// Why a request did not come back, in the page's words, from what the page knows for certain: no
    /// answer within the client's time, a connection that failed, an answer it cannot read, or an
    /// answer of the host's that is a failure. Of the host's 503s, two say which they are in a field of
    /// the problem document (<see cref="DrawioSelectionCoordinator.UnavailableReason"/>, row 34 of the
    /// queue): the engine not loaded - the filter of the "engine" group, <c>Program.cs</c>, in front of
    /// the panel's reads - and the warehouse not reachable - the cell list's route refusing with
    /// <c>WarehouseUnavailableException</c>: the boundary not set (<c>WmsEndpoints.Warehouse</c>), or no
    /// usable answer through it: no connection, a timeout, a status it did not expect, a body it cannot
    /// read (<c>HttpWarehouse</c>). The words are the ones row 29 chose for the cells and the owner
    /// confirmed (row 35); the answer's own words are the "?"'s (<see cref="Detail"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A timeout and a broken connection are said too, and not only named: before row 29 the client's
    /// own text of them was on screen, and the warehouse that does not answer in time can reach the
    /// page as exactly that - the client's timeout running out before the host's call to the warehouse
    /// does.
    /// </para>
    /// <para>
    /// Decided by the field, not by the title nor by the status: the title is the host's Italian
    /// prose, and the page does not interpret the host's words (the PLC connections in use travel as
    /// data for that reason, <c>PlcConnectionProblems.InUseExtension</c>). Until row 34 every 503 on
    /// the cell list's route was the warehouse: the engine not loaded can reach that line too - the
    /// configuration read that comes first sits behind the same filter, so it is the engine that stops
    /// being loaded between the two reads, a graph replaced by another session that fails to reload
    /// (<c>PlantQueries</c>) - and the sentence then blamed the warehouse. A 503 without the field (the
    /// engine's own database missing, or an empty body) is a failure of the host's like any other.
    /// </para>
    /// <para>
    /// Row 37 of the queue added the last two: an answer that came and cannot be read (a
    /// <see cref="JsonException"/>, which <c>WcsClient</c> does not wrap), and what none of the others
    /// is - the configuration's read and the rereads after a write let every failure through to their
    /// line, where before they quoted its message.
    /// </para>
    /// </remarks>
    public static string Why(Exception ex) => ex switch
    {
        WcsApiException fromHost when DrawioSelectionCoordinator.UnavailableReason(fromHost.Body) == ServiceUnavailableProblems.EngineNotLoaded =>
            "the engine is not ready (it is not loaded on the host)",
        WcsApiException fromHost when DrawioSelectionCoordinator.UnavailableReason(fromHost.Body) == ServiceUnavailableProblems.WarehouseUnreachable =>
            "the warehouse is not reachable from the engine",
        WcsApiException => "the host answered with an error",
        TaskCanceledException => "the host did not answer in time",
        HttpRequestException => "the connection to the host failed",
        JsonException => "the host's answer could not be read",
        _ => Unforeseen,
    };

    /// <summary>
    /// <see cref="Why"/> for a write: a 409 or a 423 is the host refusing it, and the sentence says what
    /// that status means on the route (<see cref="DrawioSelectionCoordinator.HostRefusalMeaning"/>);
    /// which of the plant's states it was, in the host's words, is behind the "?" with the rest of the
    /// answer.
    /// </summary>
    public static string WhyAWriteFailed(Exception ex) => ex is WcsApiException { StatusCode: HttpStatusCode.Conflict or HttpStatusCode.Locked } refused
        ? $"the host refused it - {DrawioSelectionCoordinator.HostRefusalMeaning(refused)}"
        : Why(ex);

    /// <summary>
    /// What a request the host turned away means, when the answer says for certain that THAT request
    /// wrote nothing; null for every other failure. A 409 or a 423 is the host refusing it
    /// (<see cref="DrawioSelectionCoordinator.HostRefusalMeaning"/>); the 503 of the engine not loaded
    /// is the filter of the "engine" group (<c>Program.cs</c>) refusing it before the endpoint runs. The
    /// one route that answers that same 503 AFTER writing - the graph's <c>PUT</c>, when the plant
    /// written does not reload (<c>PlantQueries</c>) - is the import's and the save's, never a line
    /// that asks this.
    /// </summary>
    /// <remarks>
    /// Final review, M9: the writes that make several requests - the cells' commands, the configuration
    /// with its reading back - used to say a refusal and "the request may have reached the host anyway"
    /// in one sentence. Certain of the request refused, they are not certain of the ones before it, and
    /// that is what they say instead.
    /// </remarks>
    public static string? RefusalMeaning(Exception ex) => ex switch
    {
        WcsApiException { StatusCode: HttpStatusCode.Conflict or HttpStatusCode.Locked } refused =>
            DrawioSelectionCoordinator.HostRefusalMeaning(refused),
        WcsApiException fromHost when DrawioSelectionCoordinator.UnavailableReason(fromHost.Body) == ServiceUnavailableProblems.EngineNotLoaded =>
            "the engine is not ready (it is not loaded on the host)",
        _ => null,
    };

    /// <summary>
    /// Why a call to the page's own script (<c>drawio-bridge.js</c>) did not come back with an answer,
    /// from the failure's type (final review, M7 and M8): the script threw, it did not answer within
    /// the interop's time, or the connection to the browser went. A sentence that named one of them for
    /// all three was false for the other two.
    /// </summary>
    public static string WhyTheScriptDidNotAnswer(Exception ex) => ex switch
    {
        JSDisconnectedException => "the connection to the browser was lost",
        OperationCanceledException => "the page's script did not answer in time",
        JSException => "the page's script failed",
        _ => Unforeseen,
    };

    /// <summary>
    /// What stays behind the "?": the host's answer as it came (<see cref="DrawioWriteFailure.AsItCame"/>)
    /// - its status and its body, which for the host's own refusals is the problem document with its
    /// title, its detail and the trace id that finds the request in the host's log - or, when no answer
    /// came, the failure's type and message. Nothing is parsed: whoever opens it is after exactly what
    /// came back, and the sentence has already said what it means.
    /// </summary>
    public static string Detail(Exception ex) => ex is WcsApiException fromHost
        ? DrawioWriteFailure.AsItCame(fromHost)
        : $"{ex.GetType().Name}: {ex.Message}";
}

/// <summary>
/// A line of the properties panel (row 37 of the queue): its sentence, and what came back behind its
/// "?" - null when the line says no failed request. One value, so that a line said again without a
/// failure can never keep the "?" of the one before it.
/// </summary>
public sealed record DrawioSaid(string Text, string? Detail = null)
{
    /// <summary>A line with no failure behind it: most of the panel's lines are the page's own words.</summary>
    [return: NotNullIfNotNull(nameof(text))]
    public static implicit operator DrawioSaid?(string? text) => text is null ? null : new(text);

    /// <summary>A line about <paramref name="failure"/>: what came back is behind its "?" (<see cref="DrawioFailureWords.Detail"/>).</summary>
    public static DrawioSaid Failed(string text, Exception failure) => new(text, DrawioFailureWords.Detail(failure));
}
