using System.Net;
using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Drawio;
using Wcs.WebEditor.Drawio.Coordination;
using Wcs.WebEditor.Model;
using Xunit;

namespace Wcs.WebEditor.Test;

public sealed class DrawioGeometryCoordinatorTest
{
    [Fact]
    public async Task AnUncertainOperationStopsEveryLaterOperationAndReloadsTheGraph()
    {
        var invoked = new List<string>();
        var reloads = 0;
        var graph = new PlantGraph([], [], []);
        var coordinator = new DrawioGeometryCoordinator(
            (_, _) => Task.FromResult<string?>(null),
            () => { reloads++; return Task.FromResult(new PlantGraph([], [], [])); },
            _ => { });
        var plan = new DrawioWritePlan(graph,
        [
            new DrawioWriteOperation("first", () => { invoked.Add("first"); return Task.CompletedTask; }),
            new DrawioWriteOperation("second", () => throw new HttpRequestException(
                HttpRequestError.ResponseEnded, "connection broke")),
            new DrawioWriteOperation("third", () => { invoked.Add("third"); return Task.CompletedTask; }),
        ], [], []);

        var result = await coordinator.ApplyAsync(plan);

        Assert.Equal(["first"], invoked);
        Assert.Equal(["first"], result.Written);
        Assert.Equal(["second"], result.Unconfirmed);
        Assert.Equal(["third"], result.NotWritten);
        Assert.Equal(1, reloads);
        // Row 37 of the queue, extended to the whole page on 10/10: why in the page's words, and the
        // failure's own type and message behind the line's "?".
        Assert.Equal("Save interrupted at step 2 of 3 because the connection to the host failed: whether that step went through is NOT KNOWN. " +
                     "What follows says what was written, what is not known and what never went out.", result.Error);
        Assert.Equal("HttpRequestException: connection broke", result.ErrorDetail);
    }

    /// <summary>
    /// A write the host refused stops the round with one sentence that says what the refusal means on
    /// the route, and the host's own words behind its "?" with the rest of the answer - not "(the host
    /// said: ...)" in the middle of it (row 37 of the queue, extended to the whole page on 10/10).
    /// </summary>
    [Fact]
    public async Task AWriteTheHostRefusedIsOneSentenceWithTheHostsWordsBehindIt()
    {
        var coordinator = new DrawioGeometryCoordinator(
            (_, _) => Task.FromResult<string?>(null),
            () => Task.FromResult(new PlantGraph([], [], [])),
            _ => { });
        var plan = new DrawioWritePlan(new PlantGraph([], [], []),
        [
            new DrawioWriteOperation("refused", () => throw new WcsApiException(HttpStatusCode.Conflict,
                "{\"title\":\"Il motore non e' fermo\",\"detail\":\"geometria: il motore e' in Run\"}")),
            new DrawioWriteOperation("later", () => Task.CompletedTask),
        ], [], []);

        var result = await coordinator.ApplyAsync(plan);

        Assert.Equal("Save interrupted at step 1 of 2 because the host refused it - the plant's state does not allow it now: the plant was left halfway. " +
                     "What follows says exactly what was written and what was not.", result.Error);
        Assert.Equal("the host answered 409: {\"title\":\"Il motore non e' fermo\",\"detail\":\"geometria: il motore e' in Run\"}", result.ErrorDetail);
    }

    /// <summary>The plant that cannot be read again after an interruption: the why in the notice, the host's answer behind its "?".</summary>
    [Fact]
    public async Task APlantThatCannotBeReadAgainAfterTheInterruptionKeepsTheAnswerBehindTheNotice()
    {
        var coordinator = new DrawioGeometryCoordinator(
            (_, _) => Task.FromResult<string?>(null),
            () => throw new WcsApiException(HttpStatusCode.InternalServerError, ""),
            _ => { });
        var plan = new DrawioWritePlan(new PlantGraph([], [], []),
        [
            new DrawioWriteOperation("failed", () => throw new HttpRequestException(HttpRequestError.ConnectionError, "host down")),
        ], [], []);

        var result = await coordinator.ApplyAsync(plan);

        var notice = Assert.Single(result.Notices);
        Assert.Equal("plant", notice.Id);
        Assert.StartsWith("after the interruption the plant could not be read again because the host answered with an error: without its state as of now no further save can be computed",
            notice.Message);
        Assert.Equal("the host answered 500 with an empty body", notice.Detail);
    }

    /// <summary>
    /// The permit renewed before a step that gets no answer: the notice says why in the page's words,
    /// and the host's answer is behind its "?" (row 37 of the queue, extended to the whole page on 10/10).
    /// </summary>
    [Fact]
    public async Task APermitRenewalWithNoAnswerKeepsTheAnswerBehindTheNotice()
    {
        var host = new DrawioWriteHandler { FailRoute = "edit-lock" };
        var state = new Wcs.WebEditor.Session.EditorSessionState(new Uri("http://localhost:5580"), host);
        state.UseKey("test-key");
        var coordinator = new DrawioGeometryCoordinator(state, [], () => true, () => null, _ => { }, _ => { }, _ => Task.FromResult(true));
        var plan = new DrawioWritePlan(new PlantGraph([], [], []),
        [
            new DrawioWriteOperation("only", () => Task.CompletedTask),
        ], [], []);

        var result = await coordinator.ApplyAsync(plan);

        var notice = Assert.Single(result.Notices);
        Assert.Equal("edit permit", notice.Id);
        Assert.Equal("the permit renewal before step 1 got no answer because the host answered with an error: the save carries on, and if the permit had expired the first write would stop by itself.",
            notice.Message);
        Assert.Equal("the host answered 500 with an empty body", notice.Detail);
        Assert.Equal(["only"], result.Written);
    }

    [Fact]
    public async Task AConfirmedFailureIncludesTheFailedOperationAmongNotWritten()
    {
        var graph = new PlantGraph([], [], []);
        var coordinator = new DrawioGeometryCoordinator(
            (_, _) => Task.FromResult<string?>(null),
            () => Task.FromResult(new PlantGraph([], [], [])),
            _ => { });
        var plan = new DrawioWritePlan(graph,
        [
            new DrawioWriteOperation("failed", () => throw new HttpRequestException(
                HttpRequestError.ConnectionError, "host down")),
            new DrawioWriteOperation("later", () => Task.CompletedTask),
        ], [], []);

        var result = await coordinator.ApplyAsync(plan);

        Assert.Empty(result.Unconfirmed);
        Assert.Equal(["failed", "later"], result.NotWritten);
    }

    /// <summary>
    /// Reading back what of a write did not reach the plant happens AFTER the write went through: if
    /// that reading throws, the step is still written, the save goes on to the next one, and the
    /// round is left unfinished with a line that says why - never a failed step (row 46, fix round 2).
    /// </summary>
    [Fact]
    public async Task AReadingOfTheAnswerThatThrowsDoesNotTurnAWrittenStepIntoAFailure()
    {
        var graph = new PlantGraph([], [], []);
        var invoked = new List<string>();
        var coordinator = new DrawioGeometryCoordinator(
            (_, _) => Task.FromResult<string?>(null),
            () => Task.FromResult(new PlantGraph([], [], [])),
            _ => { });
        var plan = new DrawioWritePlan(graph,
        [
            new DrawioWriteOperation("junction", () => { invoked.Add("junction"); return Task.CompletedTask; })
            {
                NotWrittenPart = () => throw new InvalidOperationException("the answer could not be read"),
            },
            new DrawioWriteOperation("later", () => { invoked.Add("later"); return Task.CompletedTask; }),
        ], [], []);

        var result = await coordinator.ApplyAsync(plan);

        Assert.Null(result.Error);
        Assert.Equal(["junction", "later"], invoked);
        Assert.Equal(["junction", "later"], result.Written);
        var line = Assert.Single(result.NotWritten);
        Assert.Contains("junction", line);
        Assert.Contains("could not be read back", line);
        // The round left a piece unwritten (compito 26/9, punto 3): the summary must not call this a
        // success, even though every write itself went through with no exception.
        Assert.Equal("Save finished: 2 writes, 1 piece not written - see below.", result.Summary);
    }

    /// <summary>
    /// A refusal decided BEFORE the loop (BuildPlanAsync, e.g. the decoration not loaded) travels on
    /// the SAME <see cref="DrawioApplyResult"/> as a plan that otherwise writes everything: the
    /// summary must say so instead of "Save succeeded" next to a refused line (compito 26/9, punto 3).
    /// </summary>
    [Fact]
    public async Task ARefusalKeepsTheSummaryHonestEvenWhenEveryOperationWrites()
    {
        var graph = new PlantGraph([], [], []);
        var coordinator = new DrawioGeometryCoordinator(
            (_, _) => Task.FromResult<string?>(null),
            () => Task.FromResult(new PlantGraph([], [], [])),
            _ => { });
        var plan = new DrawioWritePlan(graph,
        [
            new DrawioWriteOperation("geometry", () => Task.CompletedTask),
        ],
        [new DrawioRefusal("decoration", "had not arrived from the host yet")],
        []);

        var result = await coordinator.ApplyAsync(plan);

        Assert.Null(result.Error);
        Assert.Equal(["geometry"], result.Written);
        Assert.Single(result.Refusals);
        Assert.Equal("Save finished: 1 write, 1 piece not written - see below.", result.Summary);
    }

    /// <summary>
    /// Zero write operations does not mean "nothing to write" when a refusal decided before the loop
    /// (BuildPlanAsync) is still on the round: <c>SavedAsync</c> answers false for exactly this
    /// <see cref="DrawioApplyResult"/> (via <c>WroteEverything</c>), and the headline must not read
    /// like a plain no-op next to it (fix round 1, reviewer's point 2 - <see cref="ApplyAsync"/> used
    /// to check <c>operationCount == 0</c> BEFORE looking at what was missing).
    /// </summary>
    [Fact]
    public async Task AZeroOperationRoundWithARefusalIsNotCalledNothingToWrite()
    {
        var graph = new PlantGraph([], [], []);
        var coordinator = new DrawioGeometryCoordinator(
            (_, _) => Task.FromResult<string?>(null),
            () => Task.FromResult(new PlantGraph([], [], [])),
            _ => { });
        var plan = new DrawioWritePlan(graph, [],
            [new DrawioRefusal("decoration", "had not arrived from the host yet")], []);

        var result = await coordinator.ApplyAsync(plan);

        Assert.False(result.WroteEverything);
        Assert.Equal("Save finished: 0 writes, 1 piece not written - see below.", result.Summary);
    }

    /// <summary>A round with nothing refused, nothing left unwritten and nothing unconfirmed still says "Save succeeded".</summary>
    [Fact]
    public async Task ACleanRoundStillSaysSaveSucceeded()
    {
        var graph = new PlantGraph([], [], []);
        var coordinator = new DrawioGeometryCoordinator(
            (_, _) => Task.FromResult<string?>(null),
            () => Task.FromResult(new PlantGraph([], [], [])),
            _ => { });
        var plan = new DrawioWritePlan(graph,
        [
            new DrawioWriteOperation("geometry", () => Task.CompletedTask),
            new DrawioWriteOperation("junction", () => Task.CompletedTask),
        ], [], []);

        var result = await coordinator.ApplyAsync(plan);

        Assert.Equal("Save succeeded: 2 writes.", result.Summary);
    }

    /// <summary>
    /// The pure formula behind the headline, tested directly against the combinations the brief and
    /// the round-1 review ask for: <see cref="DrawioApplyResult.WroteEverything"/> is the same
    /// predicate <c>DrawioSaveCoordinator.SavedAsync</c> reads to answer true, and it is checked here
    /// even for the "unconfirmed" case that <see cref="ApplyAsync"/> itself cannot reach without also
    /// setting an error (every catch that adds to Unconfirmed returns with Error set and Summary
    /// null) - the formula must still be right if a future caller ever reaches it that way. Also
    /// covers the singular/plural wording and the split between "not written" (refused or left
    /// unwritten - the drawing's request never reached the plant) and "not confirmed" (unconfirmed -
    /// it may have gone through anyway), which used to be folded into one number and one word.
    /// </summary>
    [Theory]
    [InlineData(3, 0, 0, 0, "Save succeeded: 3 writes.")]
    [InlineData(1, 0, 0, 0, "Save succeeded: 1 write.")]
    [InlineData(3, 0, 0, 1, "Save finished: 3 writes, 1 piece not written - see below.")]
    [InlineData(2, 0, 1, 0, "Save finished: 2 writes, 1 piece not written - see below.")]
    [InlineData(1, 1, 0, 0, "Save finished: 1 write, 1 piece not confirmed - see below.")]
    [InlineData(4, 1, 1, 1, "Save finished: 4 writes, 2 pieces not written and 1 piece not confirmed - see below.")]
    [InlineData(0, 0, 0, 0, "Save: there was nothing to write.")]
    [InlineData(0, 0, 0, 1, "Save finished: 0 writes, 1 piece not written - see below.")]
    public void BuildSaveSummaryReflectsWhatWasReallyWritten(
        int operationCount, int unconfirmedCount, int notWrittenCount, int refusalCount, string expected) =>
        Assert.Equal(expected,
            DrawioGeometryCoordinator.BuildSaveSummary(operationCount, SyntheticResult(unconfirmedCount, notWrittenCount, refusalCount)));

    private static DrawioApplyResult SyntheticResult(int unconfirmedCount, int notWrittenCount, int refusalCount) => new(
        [],
        [.. Enumerable.Range(0, unconfirmedCount).Select(i => $"unconfirmed-{i}")],
        [.. Enumerable.Range(0, notWrittenCount).Select(i => $"not-written-{i}")],
        [.. Enumerable.Range(0, refusalCount).Select(i => new DrawioRefusal($"cell-{i}", "refused"))],
        [],
        new PlantGraph([], [], []),
        null,
        null);
}
