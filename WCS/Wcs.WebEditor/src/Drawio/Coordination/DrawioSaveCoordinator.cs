using System.Net;
using System.Xml;
using Wcs.Api.Client;
using Wcs.WebEditor.Drawing;
using Wcs.WebEditor.Model;
using Wcs.WebEditor.Session;

namespace Wcs.WebEditor.Drawio.Coordination;

internal sealed class DrawioSaveCoordinator
{
    private readonly DrawioPlantWriteGate _writeGate;
    private readonly Func<PlantGraph?> _openedGraph;
    private readonly Func<bool> _documentWasSent;
    private readonly Func<Task<DrawioSaid?>> _ensureWritable;
    private readonly Func<Task<PlantGraph>> _loadCurrentGraph;
    private readonly Func<DrawioContents, PlantGraph, PlantGraph, Task<DrawioApplyResult>> _execute;
    private readonly Action<PlantGraph> _adoptGraph;

    internal DrawioSaveCoordinator(
        DrawioPlantWriteGate writeGate,
        Func<PlantGraph?> openedGraph,
        Func<bool> documentWasSent,
        Func<Task<DrawioSaid?>> ensureWritable,
        Func<Task<PlantGraph>> loadCurrentGraph,
        Func<DrawioContents, PlantGraph, PlantGraph, Task<DrawioApplyResult>> execute,
        Action<PlantGraph> adoptGraph)
    {
        _writeGate = writeGate;
        _openedGraph = openedGraph;
        _documentWasSent = documentWasSent;
        _ensureWritable = ensureWritable;
        _loadCurrentGraph = loadCurrentGraph;
        _execute = execute;
        _adoptGraph = adoptGraph;
    }

    internal DrawioSaveCoordinator(
        DrawioGeometryCoordinator geometry,
        DrawioPlantWriteGate writeGate,
        Func<PlantGraph?> openedGraph,
        Action<PlantGraph> adoptGraph,
        EditorSessionState session,
        Func<bool> documentWasSent,
        Dictionary<string, Guid> plantJunctionByDrawnEdge,
        Dictionary<string, Guid> plantItemByDrawnCell,
        Func<Task<DrawioSaid?>> ensureWritable)
        : this(
            writeGate,
            openedGraph,
            documentWasSent,
            ensureWritable,
            async () =>
            {
                var graph = new PlantGraph();
                await graph.LoadAsync(session.Client);
                return graph;
            },
            async (drawn, opened, current) =>
            {
                var delta = DrawioDelta.Compute(
                    drawn, opened, current, plantJunctionByDrawnEdge, plantItemByDrawnCell);
                var plan = await geometry.PlanAsync(delta, current);
                return await geometry.ApplyAsync(plan);
            },
            adoptGraph)
    {
    }

    public DrawioSaveReportState State { get; private set; } = EmptyReport();
    public long Round { get; private set; }
    public event Action? Changed;

    /// <summary>
    /// One save round, on the drawing draw.io handed over.
    /// </summary>
    /// <returns>
    /// Whether this round wrote EVERYTHING the drawing asked for: no error, nothing refused, nothing
    /// left unwritten, nothing left unconfirmed. It is the only answer on which the bridge takes the
    /// drawing as being on the plant (the "Leave site?" mark, row 46): a round that stopped, refused
    /// a piece or could not confirm one answers false, and the close of the tab goes on asking.
    /// Computed from THIS round's own outcome and not read back from <see cref="State"/>, which a
    /// second save refused at the gate overwrites while this one is still running.
    /// </returns>
    public Task<bool> SavedAsync(string xml) => SavedAsync(() => Task.FromResult<string?>(xml));

    /// <summary>
    /// Queue item 13: the same round, on a drawing the page still has to TAKE from the browser -
    /// <paramref name="handOver"/> brings it across the circuit (see <see cref="DrawioDrawingTransfer"/>)
    /// and is called only once the write gate is held, so a save refused at the gate takes nothing.
    /// A drawing that does not arrive ends the round like any other refusal: said on the page, nothing
    /// written, and the answer false. Null is a drawing handed over empty.
    /// </summary>
    public async Task<bool> SavedAsync(Func<Task<string?>> handOver) =>
        await RoundAsync(handOver) == DrawioSaveRound.WroteEverything;

    /// <summary>
    /// The same round, with what it did to the plant: everything, nothing at all - it stopped before
    /// writing a piece, as a save refused at the gate or one whose drawing did not arrive - or less
    /// than everything with some of it possibly written. The page tells these apart when a drop took
    /// a round's answer (final fix round, C1): a round that wrote nothing is not one that "may have
    /// been written".
    /// </summary>
    public async Task<DrawioSaveRound> RoundAsync(Func<Task<string?>> handOver)
    {
        State = EmptyReport();
        DrawioPlantWriteLease? lease = null;
        try
        {
            if (!_writeGate.TryEnter(DrawioPlantWriter.Drawing, out lease))
            {
                State = State with { Error = DrawioWriteRefusal.ForDrawing(_writeGate.Current) };
                return DrawioSaveRound.WroteNothing;
            }

            string xml;
            try
            {
                xml = await handOver() ?? "";
            }
            catch (DrawioDrawingNotReceivedException ex)
            {
                // The exception's message is the page's own words for what went wrong; what the browser
                // or the circuit threw is behind the line's "?" (row 37 of the queue).
                State = State with
                {
                    Error = $"The drawing did not reach the page, and nothing was written: {ex.Message}.",
                    ErrorDetail = ex.InnerException is { } failure ? DrawioFailureWords.Detail(failure) : null,
                };
                return DrawioSaveRound.WroteNothing;
            }

            return await SaveCoreAsync(xml);
        }
        finally
        {
            lease?.Dispose();
            Round++;
            Changed?.Invoke();
        }
    }

    private async Task<DrawioSaveRound> SaveCoreAsync(string xml)
    {
        if (_openedGraph() is not { } opened)
        {
            State = State with
            {
                Error = "The plant is not loaded yet: the drawing was not written. Try again in a moment.",
            };
            return DrawioSaveRound.WroteNothing;
        }

        DrawioContents drawn;
        try
        {
            drawn = DrawioReader.Read(xml);
        }
        catch (XmlException ex)
        {
            State = State with
            {
                Error = "The drawing that came back from draw.io is not readable as XML: nothing was written.",
                ErrorDetail = DrawioFailureWords.Detail(ex),
            };
            return DrawioSaveRound.WroteNothing;
        }

        if (drawn.Diagrams > 1)
        {
            State = State with
            {
                Error = $"The drawing came back from draw.io with {drawn.Diagrams} pages, and this Editor draws the plant on ONE: everything on the pages after the first is not on the plant, would never have been written, and would be gone at the next opening. Nothing at all was written - not even what is on the first page. You may be drawing on one of those pages without knowing it, because the page tabs are hidden: Ctrl+Shift+PageDown goes to the next page and Ctrl+Shift+PageUp comes back, so go and see what is there before you do anything else. The way out is to reload - the plant comes back drawn on a single page - and it takes away whatever is on the other pages: this save refuses while there is more than one page, this Editor offers no way to delete one, and carrying that work onto the first page with Ctrl+C and Ctrl+V does not make this save accept it either.",
            };
            return DrawioSaveRound.WroteNothing;
        }

        if (!_documentWasSent())
        {
            State = State with
            {
                Error = "The drawing never received the plant (draw.io did not load it): what comes back is not the plant's drawing, and taking it at face value would erase everything. Nothing was written - reload the page.",
            };
            return DrawioSaveRound.WroteNothing;
        }

        var cellsDrawn = drawn.Devices.Count + drawn.Junctions.Count + drawn.Unknown.Count;
        var cellsSent = opened.Items.Count(item => DrawioDocument.IsDrawn(opened, item))
            + opened.Junctions.Count(junction => DrawioDocument.IsDrawn(opened, junction));
        if (cellsDrawn == 0 && cellsSent > 0)
        {
            State = State with
            {
                Error = $"The drawing that came back from draw.io contains none of the {cellsSent} plant cells that had been sent to it: it is not the plant's drawing, and writing it would erase the plant. Nothing was written. If the drawing is broken, reload the page; if instead you meant to empty the plant, this page will not take it in one go: delete the cells a few at a time, leaving at least one of them on the canvas at every save - this refusal fires only when none is left - and the last cell cannot be taken away this way at all.",
            };
            return DrawioSaveRound.WroteNothing;
        }

        if (await _ensureWritable() is { } refused)
        {
            State = State with { Error = refused.Text, ErrorDetail = refused.Detail };
            return DrawioSaveRound.WroteNothing;
        }

        PlantGraph current;
        try
        {
            current = await _loadCurrentGraph();
        }
        catch (Exception ex) when (ex is HttpRequestException or WcsApiException or TaskCanceledException)
        {
            var said = DrawioWriteFailure.Said("The plant could not be read again", ": nothing was written", ex);
            State = State with { Error = said.Text, ErrorDetail = said.Detail };
            return DrawioSaveRound.WroteNothing;
        }

        DrawioApplyResult result;
        try
        {
            result = await _execute(drawn, opened, current);
        }
        catch (Exception ex) when (ex is HttpRequestException or WcsApiException or TaskCanceledException or InvalidOperationException)
        {
            var said = DrawioWriteFailure.Said("Preparing the save failed", ": nothing was written", ex);
            State = State with { Error = said.Text, ErrorDetail = said.Detail };
            return DrawioSaveRound.WroteNothing;
        }

        State = new DrawioSaveReportState(
            result.Error,
            result.Summary,
            result.Written,
            result.Unconfirmed,
            result.NotWritten,
            result.Refusals,
            result.Notices)
        {
            ErrorDetail = result.ErrorDetail,
        };
        _adoptGraph(result.CurrentGraph);
        // WroteEverything is the SAME predicate DrawioGeometryCoordinator.BuildSaveSummary reads to
        // choose between "Save succeeded" and "Save finished ... not written" (fix round 1): defined
        // once on DrawioApplyResult so this answer and that headline can never disagree.
        // Less than everything may still be something: what the round wrote is in its report.
        return result.WroteEverything ? DrawioSaveRound.WroteEverything : DrawioSaveRound.NotEverything;
    }

    private static DrawioSaveReportState EmptyReport() =>
        new(null, null, [], [], [], [], []);
}

/// <summary>What one save round did to the plant - see <see cref="DrawioSaveCoordinator.RoundAsync"/>.</summary>
internal enum DrawioSaveRound
{
    /// <summary>Everything the drawing asked for is on the plant.</summary>
    WroteEverything,

    /// <summary>The round stopped before it wrote any piece: nothing on the plant changed.</summary>
    WroteNothing,

    /// <summary>Not everything: an error, a refusal or a piece left unwritten or unconfirmed, after some pieces may have been written.</summary>
    NotEverything,
}
