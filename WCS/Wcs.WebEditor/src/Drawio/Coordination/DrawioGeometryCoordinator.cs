using System.Net;
using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Drawing;
using Wcs.WebEditor.Editing;
using Wcs.WebEditor.Model;
using Wcs.WebEditor.Session;

namespace Wcs.WebEditor.Drawio.Coordination;

internal sealed record DrawioWriteOperation(string Label, Func<Task> WriteAsync)
{
    public Func<string>? Written { get; init; }
    public string WrittenLabel => Written?.Invoke() ?? Label;

    /// <summary>
    /// The part of what the drawing asked for that the host's answer shows did NOT reach the plant,
    /// even though the write itself went through; null when there is none. Read after the write, and
    /// reported as NOT written: a round that carries one does not count as having written
    /// everything, so the "Leave site?" mark stays on and the next save tries again.
    /// </summary>
    public Func<string?>? NotWrittenPart { get; init; }
}

internal sealed record DrawioWritePlan(
    PlantGraph Graph,
    IReadOnlyList<DrawioWriteOperation> Operations,
    IReadOnlyList<DrawioRefusal> Refusals,
    IReadOnlyList<DrawioNotice> Notices)
{
    /// <summary>
    /// The devices somebody else deleted whose cells the drawing still carries: taken off the
    /// canvas by <see cref="DrawioGeometryCoordinator.ApplyAsync"/>, which then says so - see
    /// <see cref="DrawioDeviceGone"/>.
    /// </summary>
    public IReadOnlyList<DrawioDeviceGone> GoneFromThePlant { get; init; } = [];
}

internal sealed record DrawioApplyResult(
    IReadOnlyList<string> Written,
    IReadOnlyList<string> Unconfirmed,
    IReadOnlyList<string> NotWritten,
    IReadOnlyList<DrawioRefusal> Refusals,
    IReadOnlyList<DrawioNotice> Notices,
    PlantGraph CurrentGraph,
    string? Error,
    string? Summary)
{
    /// <summary>
    /// Whether this round wrote EVERYTHING the drawing asked for: no error, nothing refused, nothing
    /// left unwritten, nothing left unconfirmed. The ONE predicate behind both
    /// <c>DrawioSaveCoordinator.SavedAsync</c>'s answer and this round's own headline
    /// (<see cref="DrawioGeometryCoordinator.BuildSaveSummary"/>) - defined once here so the two can
    /// never disagree about the same round (fix round 1, compito 26/9 punto 2: they used to be two
    /// separate computations that could drift apart).
    /// </summary>
    public bool WroteEverything => Error is null && Refusals.Count == 0 && NotWritten.Count == 0 && Unconfirmed.Count == 0;

    /// <summary>
    /// What came back for an <see cref="Error"/> about a request that failed, behind the "?" of the
    /// error's line (row 37 of the queue, extended to the whole page on 10/10); null otherwise.
    /// </summary>
    public string? ErrorDetail { get; init; }
}

internal sealed class DrawioGeometryCoordinator
{
    private readonly Func<int, IList<DrawioNotice>, Task<string?>> _renewPermit;
    private readonly Func<Task<PlantGraph>> _reloadGraph;
    private readonly Action<Exception> _recordFailure;
    private readonly Func<IReadOnlyList<string>, Task<bool>>? _takeOffTheCanvas;
    private readonly EditorSessionState? _session;
    private readonly Dictionary<string, Guid>? _plantJunctionByDrawnEdge;
    private readonly Func<bool>? _decorationLoaded;
    private readonly Func<Exception?>? _decorationFailure;
    private readonly Action<string>? _decorationWritten;

    /// <param name="takeOffTheCanvas">
    /// Takes these cells off draw.io's canvas and answers whether they are gone - the bridge's
    /// <c>removeDeletedDeviceCells</c>, which never lights the "Leave site?" mark and puts nothing
    /// into draw.io's undo history. Null where there is no canvas: the cells are then said NOT
    /// taken off, which is the side a doubt falls on.
    /// </param>
    internal DrawioGeometryCoordinator(
        Func<int, IList<DrawioNotice>, Task<string?>> renewPermit,
        Func<Task<PlantGraph>> reloadGraph,
        Action<Exception> recordFailure,
        Func<IReadOnlyList<string>, Task<bool>>? takeOffTheCanvas = null)
    {
        _renewPermit = renewPermit;
        _reloadGraph = reloadGraph;
        _recordFailure = recordFailure;
        _takeOffTheCanvas = takeOffTheCanvas;
    }

    internal DrawioGeometryCoordinator(
        EditorSessionState session,
        Dictionary<string, Guid> plantJunctionByDrawnEdge,
        Func<bool> decorationLoaded,
        Func<Exception?> decorationFailure,
        Action<string> decorationWritten,
        Action<EditLockDto> lockDenied,
        Func<IReadOnlyList<string>, Task<bool>> takeOffTheCanvas)
        : this(
            async (step, notices) =>
            {
                try
                {
                    var lease = await session.Client.RenewEditLockAsync(session.Holder);
                    if (lease.IsMine) return null;
                    lockDenied(lease);
                    return $"the edit permit is no longer this session's (somebody else has been editing the plant since {lease.SinceLocal:HH:mm:ss})";
                }
                catch (Exception ex) when (ex is HttpRequestException or WcsApiException or TaskCanceledException)
                {
                    notices.Add(new DrawioNotice("edit permit",
                        $"the permit renewal before step {step} got no answer because {DrawioFailureWords.Why(ex)}: the save carries on, and if the permit had expired the first write would stop by itself.")
                    {
                        Detail = DrawioFailureWords.Detail(ex),
                    });
                    return null;
                }
            },
            async () =>
            {
                var reloaded = new PlantGraph();
                await reloaded.LoadAsync(session.Client);
                return reloaded;
            },
            _ => { },
            takeOffTheCanvas)
    {
        _session = session;
        _plantJunctionByDrawnEdge = plantJunctionByDrawnEdge;
        _decorationLoaded = decorationLoaded;
        _decorationFailure = decorationFailure;
        _decorationWritten = decorationWritten;
    }

    public async Task<DrawioWritePlan> PlanAsync(DrawioDeltaResult delta, PlantGraph graph)
    {
        if (_session is null || _plantJunctionByDrawnEdge is null || _decorationLoaded is null
            || _decorationFailure is null || _decorationWritten is null)
            throw new InvalidOperationException("This coordinator was created without planning dependencies.");

        var operations = new List<DrawioWriteOperation>();
        var refusals = new List<DrawioRefusal>(delta.Refusals);
        var notices = new List<DrawioNotice>(delta.Notices);
        foreach (var device in delta.NewDevices)
        {
            refusals.Add(new DrawioRefusal(device.Id,
                "cell marked as a device that the plant does not know: a device is born when the shape is dropped on the canvas, and this one was not created then (the reason is in the creation report at the top of the page). The save does not create it. Select the shape and point it at a device from the properties column - or, if a new device is what you want, delete it and drag one from the panel. It was not written."));
        }
        if (delta.Moves.Count > 0)
        {
            var before = delta.Moves
                .Select(move => graph.Items.First(item => item.ItemId == move.ItemId))
                .Select(item => new PlantItemGeometryDto(item.ItemId, item.X, item.Y, item.Width, item.Height, item.Rotation))
                .ToList();
            var names = string.Join(", ", before.Select(geometry =>
                NameOf(graph.Items.First(item => item.ItemId == geometry.ItemId))));
            var label = delta.Moves.Count == 1
                ? $"the move of device {names}"
                : $"the move of {delta.Moves.Count} devices ({names})";
            operations.Add(new DrawioWriteOperation(label,
                () => new SetGeometriesCommand("Move from the drawing", before, delta.Moves).ApplyAsync(_session.Client, graph)));
        }

        foreach (var change in delta.JunctionPriorities)
        {
            if (graph.Junctions.FirstOrDefault(j => j.JunctionId == change.JunctionId) is not { } target)
            {
                notices.Add(new DrawioNotice(change.JunctionId.ToString(),
                    "junction already gone from the host while you were drawing: its priority was not changed"));
                continue;
            }
            operations.Add(new DrawioWriteOperation(
                $"the priority of junction {target.JunctionId}: from {Weight(target.Priority)} ({target.Priority}) to {Weight(change.NewPriority)} ({change.NewPriority})",
                () => new SetJunctionPriorityCommand(target, change.NewPriority).ApplyAsync(_session.Client, graph)));
        }

        foreach (var junctionId in delta.DeletedJunctions)
        {
            if (graph.Junctions.FirstOrDefault(j => j.JunctionId == junctionId) is not { } junction)
            {
                notices.Add(new DrawioNotice(junctionId.ToString(),
                    "junction already gone from the host while you were drawing: there was nothing left to delete"));
                continue;
            }
            operations.Add(new DrawioWriteOperation($"the deletion of junction {junctionId}",
                () => new DeleteJunctionCommand(junction).ApplyAsync(_session.Client, graph)));
        }

        foreach (var itemId in delta.Deleted)
        {
            if (graph.Items.FirstOrDefault(i => i.ItemId == itemId) is not { } item)
            {
                notices.Add(new DrawioNotice(itemId.ToString(),
                    "device already gone from the host while you were drawing: there was nothing left to delete"));
                continue;
            }
            var config = await _session.Client.GetItemConfigurationAsync(itemId);
            if (config is null)
            {
                refusals.Add(new DrawioRefusal(itemId.ToString(),
                    $"{item.Name}: the configuration is no longer on the host, the device was not deleted (the plant and the page are no longer aligned: reload)"));
                continue;
            }
            var geometry = new PlantItemGeometryDto(itemId, item.X, item.Y, item.Width, item.Height, item.Rotation);
            var junctions = graph.Junctions
                .Where(j => j.SourceItemId == itemId || j.DestinationItemId == itemId).ToList();
            var carried = junctions.Where(j => !delta.DeletedJunctions.Contains(j.JunctionId)).ToList();
            var alsoCarried = carried.Count == 0
                ? string.Empty
                : $" (it also carries away with it {(carried.Count == 1 ? "junction" : "junctions")} {string.Join(", ", carried.Select(j => j.JunctionId))})";
            operations.Add(new DrawioWriteOperation($"the deletion of device {NameOf(item)}{alsoCarried}",
                () => new DeleteItemCommand(config, geometry, junctions).ApplyAsync(_session.Client, graph)));
        }

        foreach (var junction in delta.NewJunctions)
        {
            var requested = junction.Priority;
            var command = new AddJunctionCommand(new JunctionRequest(junction.Source, junction.Destination, requested));
            operations.Add(new DrawioWriteOperation(
                $"the new junction {junction.Source} -> {junction.Destination}, {Weight(requested)} priority ({requested})",
                async () =>
                {
                    await command.ApplyAsync(_session.Client, graph);
                    if (command.CreatedJunctionId is { } created) _plantJunctionByDrawnEdge[junction.Id] = created;
                })
            {
                Written = () => DescribeCreatedJunction(command, graph, requested),
                NotWrittenPart = () => PriorityTheHostDidNotWrite(command, graph, requested),
            });
        }

        if (_decorationLoaded())
        {
            operations.Add(new DrawioWriteOperation("the drawing's decoration", async () =>
            {
                await _session.Client.SetPlantDecorationAsync(delta.DecorationXml);
                _decorationWritten(delta.DecorationXml);
            }));
        }
        else
        {
            var failure = _decorationFailure();
            var why = failure is not null
                ? $"had not been read at opening because {DrawioFailureWords.Why(failure)}"
                : "had not arrived from the host yet when the drawing was saved";
            refusals.Add(new DrawioRefusal("decoration",
                $"the plant's decoration {why}, so the drawing does not carry it: it is not rewritten, because rewriting it would erase the one the plant is holding. This session's free drawing - notes, boxes, unmarked shapes - is not on the plant: to carry it away before closing or reloading the page, click on the drawing, press Ctrl+A to select everything and Ctrl+C to copy it, and paste it into another draw.io with Ctrl+V.")
            {
                Detail = failure is null ? null : DrawioFailureWords.Detail(failure),
            });
        }

        return new DrawioWritePlan(graph, operations, refusals, notices)
        {
            GoneFromThePlant = delta.GoneFromThePlant,
        };
    }

    public async Task<DrawioApplyResult> ApplyAsync(DrawioWritePlan plan)
    {
        var written = new List<string>();
        var unconfirmed = new List<string>();
        var notWritten = new List<string>();
        var refusals = new List<DrawioRefusal>(plan.Refusals);
        var notices = new List<DrawioNotice>(plan.Notices);
        var currentGraph = plan.Graph;
        DrawioSaid? error = null;

        // FIRST, and whatever the writes then do: the device is already gone from the host - that is
        // what 'current' said - so taking its cells off the canvas depends on nothing this round
        // writes, and the line that says so has to be in the report on every way out of it, the
        // interrupted ones included.
        await TakeOffTheCanvasWhatSomebodyElseDeletedAsync(plan.GoneFromThePlant, refusals, notices);

        for (var i = 0; i < plan.Operations.Count; i++)
        {
            if (await _renewPermit(i + 1, notices) is { } lost)
            {
                notWritten.AddRange(plan.Operations.Skip(i).Select(step => step.Label));
                error = i == 0
                    ? $"Save did not start: {lost}. Nothing was written."
                    : $"Save interrupted before step {i + 1} of {plan.Operations.Count}: {lost}. The plant was left halfway, and what follows says exactly what was written and what was not.";
                currentGraph = await ReloadAfterFailureAsync(plan.Graph, notices);
                return Result();
            }

            try
            {
                await plan.Operations[i].WriteAsync();
                written.Add(plan.Operations[i].WrittenLabel);
            }
            catch (Exception ex) when (ex is HttpRequestException or WcsApiException or TaskCanceledException or InvalidOperationException)
            {
                _recordFailure(ex);
                var uncertain = ex is OperationCanceledException
                    || (ex is HttpRequestException transport && MayHaveBeenExecuted(transport));
                if (uncertain) unconfirmed.Add(plan.Operations[i].Label);
                notWritten.AddRange(plan.Operations.Skip(uncertain ? i + 1 : i).Select(step => step.Label));
                error = DrawioWriteFailure.Said($"Save interrupted at step {i + 1} of {plan.Operations.Count}", uncertain
                    ? ": whether that step went through is NOT KNOWN. What follows says what was written, what is not known and what never went out"
                    : ": the plant was left halfway. What follows says exactly what was written and what was not", ex);
                currentGraph = await ReloadAfterFailureAsync(plan.Graph, notices);
                return Result();
            }

            // OUTSIDE the write's try, on purpose: the write went through, and nothing this reading
            // of the host's answer does may turn it into a failed step.
            if (NotWrittenPartOf(plan.Operations[i]) is { } missing) notWritten.Add(missing);
        }

        var result = new DrawioApplyResult(written, unconfirmed, notWritten, refusals, notices, currentGraph, null, null);
        return result with { Summary = BuildSaveSummary(plan.Operations.Count, result) };

        DrawioApplyResult Result() =>
            new(written, unconfirmed, notWritten, refusals, notices, currentGraph, error?.Text, null) { ErrorDetail = error?.Detail };
    }

    /// <summary>
    /// Takes the cells of the devices somebody else deleted off the canvas, in ONE request, and
    /// writes each device's line with the canvas's answer (<see cref="DrawioDeviceGone.Sentence"/>):
    /// a notice when its cells went and nothing of the operator's was lost, a refusal otherwise.
    /// </summary>
    /// <remarks>
    /// A failure to ask is a "not taken off", never a failed save: nothing on the plant depends on
    /// it, and the operator is told to delete the shape by hand.
    /// </remarks>
    private async Task TakeOffTheCanvasWhatSomebodyElseDeletedAsync(
        IReadOnlyList<DrawioDeviceGone> gone, List<DrawioRefusal> refusals, List<DrawioNotice> notices)
    {
        if (gone.Count == 0) return;

        var cells = gone.SelectMany(device => device.DeviceCellIds.Concat(device.ArrowCellIds))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        bool takenOff;
        try
        {
            takenOff = _takeOffTheCanvas is not null && await _takeOffTheCanvas(cells);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            takenOff = false;
        }

        foreach (var device in gone)
        {
            var line = device.Sentence(takenOff);
            if (device.IsRefusal(takenOff))
                refusals.Add(new DrawioRefusal(device.ItemId.ToString(), line));
            else
                notices.Add(new DrawioNotice(device.ItemId.ToString(), line));
        }
    }

    /// <summary>
    /// The save's own headline. "Save succeeded" only when <paramref name="result"/>.
    /// <see cref="DrawioApplyResult.WroteEverything"/> is true - the SAME property
    /// <c>DrawioSaveCoordinator.SavedAsync</c> reads to answer true, so the two can never say
    /// something different about the same round (fix round 1). It is checked BEFORE
    /// <paramref name="operationCount"/> == 0: a round with nothing to write can still carry a
    /// refusal decided before the loop (the decoration not loaded, say), and "there was nothing to
    /// write" would bury that refusal under a headline that reads as a plain no-op (fix round 1,
    /// punto 2 - the bug the reviewer found: zero operations plus a refusal used to print "nothing to
    /// write" while <c>SavedAsync</c> answered false for the very same round). The refused,
    /// not-written and unconfirmed pieces are still reported line by line below this headline - this
    /// only decides the words at the top.
    /// </summary>
    internal static string BuildSaveSummary(int operationCount, DrawioApplyResult result)
    {
        if (!result.WroteEverything)
        {
            var notWrittenOrRefused = result.NotWritten.Count + result.Refusals.Count;
            return $"Save finished: {Plural(operationCount, "write")}, " +
                $"{MissingClause(notWrittenOrRefused, result.Unconfirmed.Count)} - see below.";
        }

        return operationCount == 0 ? "Save: there was nothing to write." : $"Save succeeded: {Plural(operationCount, "write")}.";
    }

    /// <summary>
    /// The words for what is missing: refusals and not-written pieces are both "not written" (the
    /// drawing's own request never reached the plant), while an unconfirmed piece is a DIFFERENT
    /// fact - the request went out and got no answer, so it may have gone through anyway - and the
    /// two are told apart here instead of being folded into one number that would say "not written"
    /// about a piece that, for all this round knows, was.
    /// </summary>
    private static string MissingClause(int notWrittenOrRefused, int unconfirmedCount)
    {
        if (unconfirmedCount == 0) return $"{Plural(notWrittenOrRefused, "piece")} not written";
        if (notWrittenOrRefused == 0) return $"{Plural(unconfirmedCount, "piece")} not confirmed";
        return $"{Plural(notWrittenOrRefused, "piece")} not written and {Plural(unconfirmedCount, "piece")} not confirmed";
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private async Task<PlantGraph> ReloadAfterFailureAsync(PlantGraph fallback, IList<DrawioNotice> notices)
    {
        try
        {
            return await _reloadGraph();
        }
        catch (Exception ex) when (ex is HttpRequestException or WcsApiException or TaskCanceledException)
        {
            notices.Add(new DrawioNotice("plant",
                $"after the interruption the plant could not be read again because {DrawioFailureWords.Why(ex)}: without its state as of now no further save can be computed, so the page has to be reloaded. Reloading, though, throws away everything the canvas carries that is not on the plant - the refused cells and the free drawing (notes, boxes, unmarked shapes): before reloading, click on the drawing, press Ctrl+A then Ctrl+C, and paste it into another draw.io with Ctrl+V.")
            {
                Detail = DrawioFailureWords.Detail(ex),
            });
            return fallback;
        }
    }

    private static bool MayHaveBeenExecuted(HttpRequestException transport) =>
        transport.HttpRequestError is not (HttpRequestError.NameResolutionError
            or HttpRequestError.ConnectionError
            or HttpRequestError.SecureConnectionError
            or HttpRequestError.ProxyTunnelError);

    private static string DescribeCreatedJunction(AddJunctionCommand command, PlantGraph graph, int requested)
    {
        if (command.CreatedJunctionId is not { } id
            || graph.Junctions.FirstOrDefault(j => j.JunctionId == id) is not { } created)
            return "the new junction - the host did not say with which identifier and with which priority it ended up";
        var label = $"junction {id}: {created.SourceItemId} -> {created.DestinationItemId}, {Weight(created.Priority)} priority ({created.Priority})";
        return created.Priority == requested
            ? label
            : $"{label} - a junction between these two devices already existed and the host returned that one without touching it";
    }

    /// <summary>
    /// The priority the drawing asked for a new junction, when the host collapsed the POST onto a
    /// junction the pair already had and left that one's priority as it was. The junction is on the
    /// plant (the written line says which one); its priority, as drawn, is not - and that is a piece
    /// of the drawing missing from the plant, so it goes to NOT written. The drawn arrow is now tied
    /// to that junction, so the next save sees a priority change and writes it.
    /// </summary>
    /// <remarks>
    /// The comparison is between WEIGHTS, through <see cref="JunctionWeights.FromPriority"/> - the one
    /// rule DrawioDelta and DrawioDocument use - and not between numbers: a junction the host kept at
    /// 50 IS high, exactly what an arrow drawn high (0) asks for, and the next save would find no
    /// difference to write. Calling it NOT written would be false and would keep the round
    /// unfinished for ever.
    /// </remarks>
    private static string? PriorityTheHostDidNotWrite(AddJunctionCommand command, PlantGraph graph, int requested)
    {
        if (command.CreatedJunctionId is not { } id
            || graph.Junctions.FirstOrDefault(j => j.JunctionId == id) is not { } created
            || JunctionWeights.FromPriority(created.Priority) == JunctionWeights.FromPriority(requested))
            return null;
        return $"the {Weight(requested)} priority ({requested}) the drawing asks for junction {id}: a junction between these two devices already existed and the host returned that one without touching it, so it is still at {Weight(created.Priority)} ({created.Priority}). Save again to write the priority as drawn.";
    }

    /// <summary>
    /// What of a write that went through did not reach the plant, read defensively: a reading that
    /// throws cannot say the piece is there, so it is reported as not known to be written - the
    /// round stays unfinished, which is the side a doubt falls on - instead of failing the step.
    /// </summary>
    private static string? NotWrittenPartOf(DrawioWriteOperation operation)
    {
        try
        {
            return operation.NotWrittenPart?.Invoke();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return $"{operation.Label}: the step went through, but whether all of it reached the plant could not be read back from the host's answer ({ex.Message}). Save again.";
        }
    }

    private static string NameOf(PlantItemDto item) =>
        item.Name is { Length: > 0 } name ? name : item.ItemId.ToString();

    private static string Weight(int priority) =>
        JunctionWeights.FromPriority(priority) == JunctionWeight.High ? "high" : "low";
}
