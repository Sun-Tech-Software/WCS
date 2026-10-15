using System.Net;
using Wcs.Api.Client;
using Wcs.WebEditor.Drawio;
using Wcs.WebEditor.Drawio.Coordination;
using Wcs.WebEditor.Model;
using Xunit;

namespace Wcs.WebEditor.Test;

public sealed class DrawioSaveCoordinatorTest
{
    [Fact]
    public async Task MalformedXmlIsReportedAndCompletesExactlyOneRound()
    {
        var coordinator = Coordinator(execute: (_, _, _) => throw new InvalidOperationException("must not execute"));
        var changes = 0;
        coordinator.Changed += () => changes++;

        await coordinator.SavedAsync("<not-closed");

        Assert.Contains("not readable as XML", coordinator.State.Error);
        Assert.Equal(1, coordinator.Round);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task AConcurrentWriterRefusesTheSaveAndStillCompletesOneRound()
    {
        var gate = new DrawioPlantWriteGate();
        Assert.True(gate.TryEnter(DrawioPlantWriter.Panel, out var held));
        using (held!)
        {
            var coordinator = Coordinator(gate);

            await coordinator.SavedAsync(ValidDrawing());

            Assert.Equal(DrawioWriteRefusal.ForDrawing(DrawioPlantWriter.Panel), coordinator.State.Error);
            Assert.Equal(1, coordinator.Round);
        }
    }

    [Theory]
    [InlineData("Read only: the engine is running - stop it before changing the plant. Nothing was written.")]
    [InlineData("Read only: the engine status is not known, and to be safe no write is made. Nothing was written.")]
    public async Task AWritePreconditionRefusalIsTheWholeReport(string refusal)
    {
        var coordinator = Coordinator(ensureWritable: () => Task.FromResult<DrawioSaid?>(refusal));

        await coordinator.SavedAsync(ValidDrawing());

        Assert.Equal(refusal, coordinator.State.Error);
        Assert.Empty(coordinator.State.Written);
        Assert.Equal(1, coordinator.Round);
    }

    [Fact]
    public async Task TheGeometryOutcomeIsPublishedAndItsCurrentGraphIsAdopted()
    {
        var replacement = new PlantGraph([], [], []);
        PlantGraph? adopted = null;
        var result = new DrawioApplyResult(
            ["written"], ["uncertain"], ["later"],
            [new DrawioRefusal("cell", "refused")],
            [new DrawioNotice("junction", "notice")],
            replacement, "stopped", null)
        {
            ErrorDetail = "the host answered 500 with an empty body",
        };
        var coordinator = Coordinator(
            execute: (_, _, _) => Task.FromResult(result),
            adopt: graph => adopted = graph);

        await coordinator.SavedAsync(ValidDrawing());

        Assert.Same(replacement, adopted);
        Assert.Equal(["written"], coordinator.State.Written);
        Assert.Equal(["uncertain"], coordinator.State.Unconfirmed);
        Assert.Equal(["later"], coordinator.State.NotWritten);
        Assert.Single(coordinator.State.Refusals);
        Assert.Single(coordinator.State.Notices);
        Assert.Equal("stopped", coordinator.State.Error);
        Assert.Equal("the host answered 500 with an empty body", coordinator.State.ErrorDetail);
        Assert.Equal(1, coordinator.Round);
    }

    // Row 37 of the queue, extended to the whole page by the owner's answer of 10/10: each line of the
    // save that says a failure says why in the page's words, in one sentence, and what came back - the
    // host's answer as it came, or the failure's own type and message - is behind its "?", not in it.

    [Fact]
    public async Task APlantThatCannotBeReadAgainIsOneSentenceWithTheHostsAnswerBehindIt()
    {
        var coordinator = Coordinator(loadCurrent: () => throw new WcsApiException(HttpStatusCode.InternalServerError, "{\"title\":\"Errore interno\"}"));

        Assert.False(await coordinator.SavedAsync(ValidDrawing()));

        Assert.Equal("The plant could not be read again because the host answered with an error: nothing was written.", coordinator.State.Error);
        Assert.Equal("the host answered 500: {\"title\":\"Errore interno\"}", coordinator.State.ErrorDetail);
    }

    [Fact]
    public async Task APreparationThatFailsIsOneSentenceWithTheFailureBehindIt()
    {
        var coordinator = Coordinator(execute: (_, _, _) => throw new TaskCanceledException("Il client ha atteso troppo (prova)."));

        Assert.False(await coordinator.SavedAsync(ValidDrawing()));

        Assert.Equal("Preparing the save failed because the host did not answer in time: nothing was written.", coordinator.State.Error);
        Assert.Equal("TaskCanceledException: Il client ha atteso troppo (prova).", coordinator.State.ErrorDetail);
    }

    [Fact]
    public async Task ADrawingThatIsNotXmlIsOneSentenceWithTheParsersWordsBehindIt()
    {
        var coordinator = Coordinator();

        await coordinator.SavedAsync("<not-closed");

        Assert.Equal("The drawing that came back from draw.io is not readable as XML: nothing was written.", coordinator.State.Error);
        Assert.StartsWith("XmlException: ", coordinator.State.ErrorDetail);
    }

    [Fact]
    public async Task WhatTheBrowserThrewIsBehindTheLineOfADrawingThatDidNotArrive()
    {
        var coordinator = Coordinator();

        await coordinator.SavedAsync(() => throw new DrawioDrawingNotReceivedException(
            "the browser did not hand the drawing over, because the connection to the browser was lost",
            new Microsoft.JSInterop.JSDisconnectedException("il circuito non c'e' piu' (prova)")));

        Assert.Equal("The drawing did not reach the page, and nothing was written: the browser did not hand the drawing over, because the connection to the browser was lost.",
            coordinator.State.Error);
        Assert.Equal("JSDisconnectedException: il circuito non c'e' piu' (prova)", coordinator.State.ErrorDetail);
    }

    [Fact]
    public async Task ASecondSaveReplacesThePreviousReportAndAdvancesOneRound()
    {
        var calls = 0;
        var graph = new PlantGraph([], [], []);
        var coordinator = Coordinator(execute: (_, _, _) =>
        {
            calls++;
            return Task.FromResult(calls == 1
                ? new DrawioApplyResult([], [], ["first"], [], [], graph, "failed", null)
                : new DrawioApplyResult(["second"], [], [], [], [], graph, null, "succeeded"));
        });

        await coordinator.SavedAsync(ValidDrawing());
        await coordinator.SavedAsync(ValidDrawing());

        Assert.Equal(2, coordinator.Round);
        Assert.Null(coordinator.State.Error);
        Assert.Equal("succeeded", coordinator.State.Summary);
        Assert.Equal(["second"], coordinator.State.Written);
        Assert.Empty(coordinator.State.NotWritten);
    }

    /// <summary>
    /// The answer the bridge puts the "Leave site?" mark down on (row 46): a round that wrote
    /// everything answers true. A notice does not stand in the way - it is something that WAS
    /// written, only not quite as the drawing might suggest.
    /// </summary>
    [Fact]
    public async Task ARoundThatWroteEverythingAnswersTrue()
    {
        var coordinator = Coordinator(execute: (_, _, current) => Task.FromResult(
            new DrawioApplyResult(["written"], [], [], [], [new DrawioNotice("junction", "a choice made")], current, null, "done")));

        Assert.True(await coordinator.SavedAsync(ValidDrawing()));
    }

    /// <summary>
    /// Every other end answers false, one missing piece at a time: each of the four is on its own a
    /// piece of the drawing that is not on the plant, and closing the tab must go on asking.
    /// </summary>
    [Theory]
    [InlineData("error")]
    [InlineData("refused")]
    [InlineData("not written")]
    [InlineData("unconfirmed")]
    public async Task ARoundThatLeftAnyPieceOutAnswersFalse(string missing)
    {
        var coordinator = Coordinator(execute: (_, _, current) => Task.FromResult(new DrawioApplyResult(
            ["written"],
            missing == "unconfirmed" ? ["no answer"] : [],
            missing == "not written" ? ["later"] : [],
            missing == "refused" ? [new DrawioRefusal("cell", "refused")] : [],
            [],
            current,
            missing == "error" ? "stopped" : null,
            "summary")));

        Assert.False(await coordinator.SavedAsync(ValidDrawing()));
    }

    /// <summary>The rounds that end before anything is written answer false too.</summary>
    [Fact]
    public async Task ARoundThatStoppedBeforeWritingAnswersFalse()
    {
        Assert.False(await Coordinator().SavedAsync("<not-closed"));
        Assert.False(await Coordinator(ensureWritable: () => Task.FromResult<DrawioSaid?>("Read only.")).SavedAsync(ValidDrawing()));

        var gate = new DrawioPlantWriteGate();
        Assert.True(gate.TryEnter(DrawioPlantWriter.Panel, out var held));
        using (held!)
            Assert.False(await Coordinator(gate).SavedAsync(ValidDrawing()));
    }

    /// <summary>
    /// The answer is THIS round's, not the shared report's. A second save that arrives while the
    /// first is writing is refused at the gate and overwrites <see cref="DrawioSaveCoordinator.State"/>
    /// with its refusal; the first round, ending clean afterwards, still answers true - and the
    /// refused one false.
    /// </summary>
    [Fact]
    public async Task EachRoundAnswersForItselfWhenASecondSaveIsRefusedMeanwhile()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = Coordinator(execute: async (_, _, current) =>
        {
            reached.SetResult();
            await release.Task;
            return new DrawioApplyResult(["written"], [], [], [], [], current, null, "done");
        });

        var first = coordinator.SavedAsync(ValidDrawing());
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(await coordinator.SavedAsync(ValidDrawing()));
        Assert.NotNull(coordinator.State.Error);

        release.SetResult();
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    /// <summary>
    /// Queue item 13: a drawing the page could not take from the browser - over the page's ceiling,
    /// or a circuit that dropped in the middle - ends the round as a refusal does: one round, the
    /// reason on the page, nothing written, and the answer false, so "Leave site?" goes on asking.
    /// </summary>
    [Fact]
    public async Task ADrawingThatDoesNotReachThePageEndsTheRoundWithTheReason()
    {
        var coordinator = Coordinator(execute: (_, _, _) => throw new InvalidOperationException("must not execute"));
        var changes = 0;
        coordinator.Changed += () => changes++;

        var answer = await coordinator.SavedAsync(
            () => throw new DrawioDrawingNotReceivedException("the drawing weighs 20.0 MB (test)"));

        Assert.False(answer);
        Assert.Equal("The drawing did not reach the page, and nothing was written: the drawing weighs 20.0 MB (test).",
            coordinator.State.Error);
        // The page's own finding: nothing came back, so there is no "?".
        Assert.Null(coordinator.State.ErrorDetail);
        Assert.Equal(1, coordinator.Round);
        Assert.Equal(1, changes);
    }

    /// <summary>The page's sentence ends the reason: one period, even when the reason brought its own.</summary>
    [Fact]
    public async Task AReasonThatEndsWithAPeriodLeavesOnlyOne()
    {
        var coordinator = Coordinator();

        await coordinator.SavedAsync(() => throw new DrawioDrawingNotReceivedException("the circuit is gone (test)."));

        Assert.Equal("The drawing did not reach the page, and nothing was written: the circuit is gone (test).",
            coordinator.State.Error);
    }

    /// <summary>
    /// The drawing is taken only once the gate is held: a save refused because somebody else is
    /// writing brings nothing across the circuit.
    /// </summary>
    [Fact]
    public async Task ASaveRefusedAtTheGateDoesNotTakeTheDrawing()
    {
        var gate = new DrawioPlantWriteGate();
        Assert.True(gate.TryEnter(DrawioPlantWriter.Panel, out var held));
        using (held!)
        {
            var coordinator = Coordinator(gate);
            var taken = 0;

            Assert.False(await coordinator.SavedAsync(() =>
            {
                taken++;
                return Task.FromResult<string?>(ValidDrawing());
            }));

            Assert.Equal(0, taken);
            Assert.Equal(DrawioWriteRefusal.ForDrawing(DrawioPlantWriter.Panel), coordinator.State.Error);
        }
    }

    /// <summary>
    /// A drawing taken from the browser is saved exactly as one handed over as text - and none at all
    /// is read as an empty text, which is not XML.
    /// </summary>
    [Fact]
    public async Task ADrawingTakenFromTheBrowserIsSavedAsTheText()
    {
        DrawioContents? read = null;
        var coordinator = Coordinator(execute: (drawn, _, current) =>
        {
            read = drawn;
            return Task.FromResult(new DrawioApplyResult(["written"], [], [], [], [], current, null, "done"));
        });

        Assert.True(await coordinator.SavedAsync(() => Task.FromResult<string?>(ValidDrawing())));
        Assert.NotNull(read);

        Assert.False(await coordinator.SavedAsync(() => Task.FromResult<string?>(null)));
        Assert.Contains("not readable as XML", coordinator.State.Error);
    }

    private static DrawioSaveCoordinator Coordinator(
        DrawioPlantWriteGate? gate = null,
        Func<Task<DrawioSaid?>>? ensureWritable = null,
        Func<DrawioContents, PlantGraph, PlantGraph, Task<DrawioApplyResult>>? execute = null,
        Action<PlantGraph>? adopt = null,
        Func<Task<PlantGraph>>? loadCurrent = null)
    {
        var opened = new PlantGraph([], [], []);
        return new DrawioSaveCoordinator(
            gate ?? new DrawioPlantWriteGate(),
            () => opened,
            () => true,
            ensureWritable ?? (() => Task.FromResult<DrawioSaid?>(null)),
            loadCurrent ?? (() => Task.FromResult(new PlantGraph([], [], []))),
            execute ?? ((_, _, current) => Task.FromResult(
                new DrawioApplyResult([], [], [], [], [], current, null, "nothing"))),
            adopt ?? (_ => { }));
    }

    private static string ValidDrawing() => DrawioDocument.Build(new PlantGraph([], [], []), null);
}
