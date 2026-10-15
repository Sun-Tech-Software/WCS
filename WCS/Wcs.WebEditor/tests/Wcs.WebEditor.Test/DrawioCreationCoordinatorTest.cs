using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Drawio;
using Wcs.WebEditor.Drawio.Coordination;
using Wcs.WebEditor.Model;
using Wcs.WebEditor.Session;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

public sealed class DrawioCreationCoordinatorTest
{
    private static DeviceTypeDto Automation() => new(
        "automation", (short)DeviceTypes.Automation, "Automation",
        [new ShapePartDto("M 4 4 H 96 V 96 H 4 Z", "#334155", Filled: false, Rigid: false)],
        [], new ShapePointDto(50, 50), new ShapePointDto(88, 60));

    private static DrawioAddedCell Dropped(string id, string? type = "0") => new(
        id, "device", type, DrawioDocument.DefaultLayerId, 10, 20, 130, 184);

    private static (DrawioCreationCoordinator Coordinator, DrawioWriteHandler Host,
        PlantGraph Graph, DrawioPlantWriteGate Gate, Dictionary<string, Guid> Links) Create()
    {
        var host = new DrawioWriteHandler { Types = [Automation()] };
        var state = new EditorSessionState(new Uri("http://localhost:5580"), host);
        state.UseKey("test-key");
        var graph = new PlantGraph([], [], [Automation()]);
        var gate = new DrawioPlantWriteGate();
        var links = new Dictionary<string, Guid>();
        var coordinator = new DrawioCreationCoordinator(
            state,
            gate,
            () => graph,
            links,
            () => Task.FromResult<DrawioSaid?>(null),
            (_, _) => Task.FromResult<DrawioSaid?>(null),
            () => Task.CompletedTask);
        return (coordinator, host, graph, gate, links);
    }

    [Fact]
    public async Task AValidDropCreatesTheDeviceAndRecordsItsCanvasLink()
    {
        var (coordinator, host, graph, _, links) = Create();

        await coordinator.CellsAddedAsync([Dropped("cell-1")]);

        Assert.Single(host.ItemCreations);
        Assert.Single(host.GeometryWrites);
        Assert.Single(graph.Items);
        Assert.True(links.ContainsKey("cell-1"));
        var line = Assert.Single(coordinator.State.Created);
        Assert.True(line.CanvasFollowed);
        Assert.Contains("Automation", line.Text);
    }

    [Fact]
    public async Task ACellWithoutADeclaredTypeIsRefusedWithoutWriting()
    {
        var (coordinator, host, _, _, _) = Create();

        await coordinator.CellsAddedAsync([Dropped("cell-1", type: null)]);

        Assert.Empty(host.ItemCreations);
        Assert.Contains("does not say which device type", Assert.Single(coordinator.State.NotCreated).Text);
    }

    [Fact]
    public async Task AnotherWriterProducesTheExactCreationRefusalAndKeepsOwnership()
    {
        var (coordinator, host, _, gate, _) = Create();
        Assert.True(gate.TryEnter(DrawioPlantWriter.Drawing, out var drawing));

        await coordinator.CellsAddedAsync([Dropped("cell-1")]);

        Assert.Empty(host.ItemCreations);
        Assert.Equal(DrawioPlantWriter.Drawing, gate.Current);
        Assert.Equal(DrawioWriteRefusal.ForCreation(DrawioPlantWriter.Drawing), coordinator.State.Error);
        drawing!.Dispose();
    }

    [Fact]
    public async Task AHostFailureBecomesAReportedFailureAndReleasesTheGate()
    {
        var (coordinator, host, _, gate, _) = Create();
        host.FailRoute = "item-post";

        await coordinator.CellsAddedAsync([Dropped("cell-1")]);

        var line = Assert.Single(coordinator.State.NotCreated);
        Assert.StartsWith("cell-1: the device was not created, or not completely, because the host answered with an error: if any part of it got through it is ", line.Text);
        Assert.Equal("the host answered 500 with an empty body", line.Detail);
        Assert.Equal(DrawioPlantWriter.Nobody, gate.Current);
    }

    // Row 37 of the queue, extended to the whole page by the owner's answer of 10/10: every line of the
    // creation that says a failure says why in the page's words, and keeps what came back - the host's
    // answer, or the failure's type and message - behind its "?" (DrawioSaid.Detail), not in it.

    [Fact]
    public async Task ACreationWithNoAnswerIsNotKnownAndKeepsTheFailureBehindTheLine()
    {
        var (coordinator, host, _, _, _) = Create();
        host.TimeoutRoute = "item-post";

        await coordinator.CellsAddedAsync([Dropped("cell-1")]);

        var line = Assert.Single(coordinator.State.NotCreated);
        Assert.StartsWith("cell-1: the request got no answer because the host did not answer in time, and whether the device was created is NOT KNOWN - if it was, it is ", line.Text);
        Assert.DoesNotContain("(prova)", line.Text);
        Assert.Equal("TaskCanceledException: La richiesta su 'item-post' e' scaduta senza risposta (prova).", line.Detail);
        Assert.Equal(1, coordinator.State.NotKnown);
    }

    [Fact]
    public async Task ADeviceTheHostDoesNotHaveRightAfterCreatingItIsSaidInThePagesWords()
    {
        var (coordinator, host, _, _, _) = Create();
        host.HideSingleItemReads = true;

        await coordinator.CellsAddedAsync([Dropped("cell-1")]);

        var line = Assert.Single(coordinator.State.NotCreated);
        // Final fix round, C3: the command's own finding, in its own words, with nothing behind a "?"
        // (DrawioWriteFailure.Said) - one way for every finding of the command, not a second copy that
        // said "the host does not have it any more" for all of them.
        Assert.StartsWith("cell-1: the device was not created, or not completely, because the device just created (", line.Text);
        Assert.Contains(") is not on the host any more: the creation is inconsistent: if any part of it got through it is ", line.Text);
        Assert.Null(line.Detail);
        Assert.Equal(1, coordinator.State.NotKnown);
    }

    /// <summary>
    /// Final fix round, C3: the host wrote the geometry and confirmed it with an empty version. The
    /// device is on the host in full: the line says what the page found, not that the host does not
    /// have it any more.
    /// </summary>
    [Fact]
    public async Task AGeometryConfirmationThePageDoesNotTakeIsSaidAsWhatItIs()
    {
        var (coordinator, host, _, _, _) = Create();
        host.ConfirmGeometryWithAnEmptyVersion = true;

        await coordinator.CellsAddedAsync([Dropped("cell-1")]);

        Assert.Single(host.GeometryWrites);
        var line = Assert.Single(coordinator.State.NotCreated);
        Assert.StartsWith("cell-1: the device was not created, or not completely, because the host confirmed an empty geometry version: if any part of it got through it is ", line.Text);
        Assert.DoesNotContain("does not have it any more", line.Text);
        Assert.Null(line.Detail);
    }

    /// <summary>
    /// Final fix round, C4: a geometry conflict on the creation's PUT is one sentence - the why where
    /// the creation's head expects it, then the tail - and not the head and the tail back to back.
    /// </summary>
    [Fact]
    public async Task AGeometryConflictOnTheCreationIsOneSentence()
    {
        var (coordinator, host, _, _, _) = Create();
        host.GeometryConflictItemIds = [Guid.Parse("33333333-3333-3333-3333-333333333333")];

        await coordinator.CellsAddedAsync([Dropped("cell-1")]);

        var line = Assert.Single(coordinator.State.NotCreated);
        Assert.StartsWith("cell-1: the device was not created, or not completely, because the geometry changed elsewhere for 33333333-3333-3333-3333-333333333333: if any part of it got through it is ", line.Text);
        Assert.EndsWith("Go and look for that identifier before dragging the shape again. No automatic retry was made, because it could overwrite the newer position, size or rotation. Reload the plant before saving again.", line.Text);
        Assert.DoesNotContain(",:", line.Text);
        Assert.Null(line.Detail);
        Assert.Equal(1, coordinator.State.NotKnown);
    }

    /// <summary>
    /// Final fix round, C4: a failure none of the page's words name stops the creation "on an unforeseen
    /// failure", and not "because an unforeseen failure stopped it"; what it was is behind the "?".
    /// </summary>
    [Fact]
    public async Task ACreationStoppedByAnUnforeseenFailureSaysSoOnce()
    {
        var host = new DrawioWriteHandler { Types = [Automation()] };
        var state = new EditorSessionState(new Uri("http://localhost:5580"), host);
        state.UseKey("test-key");
        var graph = new PlantGraph([], [], [Automation()]);
        var coordinator = new DrawioCreationCoordinator(
            state,
            new DrawioPlantWriteGate(),
            () => graph,
            new Dictionary<string, Guid>(),
            () => throw new NotSupportedException("something nobody expected (test)"),
            (_, _) => Task.FromResult<DrawioSaid?>(null),
            () => Task.CompletedTask);

        await coordinator.CellsAddedAsync([Dropped("cell-1")]);

        Assert.Equal("The creation stopped on an unforeseen failure.", coordinator.State.Error);
        Assert.Equal("NotSupportedException: something nobody expected (test)", coordinator.State.ErrorDetail);
        Assert.Empty(host.ItemCreations);
    }

    [Fact]
    public async Task ACreationStoppedByAFailureItDoesNotKnowIsOneSentenceWithTheFailureBehindIt()
    {
        var host = new DrawioWriteHandler { Types = [Automation()] };
        var state = new EditorSessionState(new Uri("http://localhost:5580"), host);
        state.UseKey("test-key");
        var graph = new PlantGraph([], [], [Automation()]);
        var coordinator = new DrawioCreationCoordinator(
            state,
            new DrawioPlantWriteGate(),
            () => graph,
            new Dictionary<string, Guid>(),
            () => throw new HttpRequestException("Connessione rifiutata (prova)."),
            (_, _) => Task.FromResult<DrawioSaid?>(null),
            () => Task.CompletedTask);

        await coordinator.CellsAddedAsync([Dropped("cell-1")]);

        Assert.Equal("The creation stopped because the connection to the host failed.", coordinator.State.Error);
        Assert.Equal("HttpRequestException: Connessione rifiutata (prova).", coordinator.State.ErrorDetail);
        Assert.Empty(host.ItemCreations);
    }
}
