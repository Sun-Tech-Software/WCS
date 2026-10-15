using System.Net;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Drawio.Components;
using Wcs.WebEditor.Pages;
using Wcs.WebEditor.Session;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// Compito 3, row 60: the list of the active missions (WinForms <c>FrmMISSIONS_LU</c>), followed on
/// the live channel, and the move of a mission onto another device - through the WHOLE page, because
/// the list's reads depend on the section being open, on the page's feed and on the drawing's
/// selection.
/// </summary>
public sealed class DrawioEditorMissionListTest : PlantCanvasBunitContext
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");
    private static readonly Guid C = Guid.Parse("cccccccc-0000-4000-8000-000000000003");
    private static readonly Guid M1 = Guid.Parse("11111111-0000-4000-8000-000000000001");
    private static readonly Guid M2 = Guid.Parse("22222222-0000-4000-8000-000000000002");
    private static readonly Guid M3 = Guid.Parse("33333333-0000-4000-8000-000000000003");
    private static readonly Guid D = Guid.Parse("dddddddd-0000-4000-8000-000000000004");
    private static readonly Guid S = Guid.Parse("55555555-0000-4000-8000-000000000005");
    private static readonly Guid W2 = Guid.Parse("66666666-0000-4000-8000-000000000006");

    private static readonly string[] CompactColumns =
    [
        nameof(MissionDto.LoadUnitId), nameof(MissionDto.LoadUnitCode), nameof(MissionDto.PositionItemId),
        nameof(MissionDto.SourceItemId), nameof(MissionDto.DestinationItemId), nameof(MissionDto.Status),
        nameof(MissionDto.StorageDoorCode), nameof(MissionDto.PickUpDoorCode), nameof(MissionDto.MissionType),
        nameof(MissionDto.OperationType),
    ];

    private readonly DrawioWriteHandler _host = new();
    private readonly FakePlantEventSource _source = new();
    private BunitJSModuleInterop _bridge = null!;

    public DrawioEditorMissionListTest()
    {
        _host.Items = [Item(A, "Shuttle 1", 10), Item(B, "Shuttle 2", 300), Item(C, "Lift", 600)];
        _host.Missions = [TestMissions.Mission(M1, A, "LU-001"), TestMissions.Mission(M2, B, "LU-002")];
    }

    private static PlantItemDto Item(Guid id, string name, double x, DeviceTypes type = DeviceTypes.Automation) =>
        new(id, name, type, null, null, null, ConnectionTypes.Test,
            default, 0, null, 0, 0, 0, 0, default!, x, 10, 100, 100, 0);

    /// <summary>A junction of the plant: the fake host's routes go along these (DrawioWriteHandler.RouteOf).</summary>
    private static JunctionDto Junction(Guid from, Guid to) => new(Guid.NewGuid(), from, to, 99);

    private IRenderedComponent<DrawioEditor> RenderPage()
    {
        _bridge = JSInterop.SetupModule("./_content/Wcs.WebEditor/drawio-bridge.js");
        _bridge.Setup<string?>("selectCell", _ => true).SetResult("ok");
        var state = new EditorSessionState(new Uri("http://localhost:5580"), _host) { EventSourceForTesting = () => _source };
        state.UseKey("test-key");
        Services.AddSingleton(state);
        var cut = Render<DrawioEditor>();
        cut.WaitForAssertion(() => Assert.False(cut.Find("#drawio-mode-toggle").HasAttribute("disabled")));
        // The channel is open and the list holds the page's feed: from here an event reaches it.
        cut.WaitForAssertion(() => Assert.NotNull(cut.FindComponent<DrawioMissionList>().Instance.Feed));
        return cut;
    }

    /// <summary>Finds and triggers in ONE step on the renderer's dispatcher: see DrawioEditorDeviceListTest.Act.</summary>
    private static void Act(IRenderedComponent<DrawioEditor> cut, Action gesture) =>
        cut.InvokeAsync(gesture).GetAwaiter().GetResult();

    private int MissionReads => _host.Requests.Count(r => r == "GET /api/v1/missions");

    private void OpenTheList(IRenderedComponent<DrawioEditor> cut)
    {
        Act(cut, () => cut.Find("#drawio-mission-list-toggle").Click());
        cut.WaitForAssertion(() => Assert.Equal(Math.Min(_host.Missions.Count, 10), Rows(cut).Count));
    }

    private static IReadOnlyList<string> Rows(IRenderedComponent<DrawioEditor> cut) =>
        [.. cut.FindAll("#drawio-mission-list-table tbody tr").Select(row => row.GetAttribute("data-mission-id") ?? "")];

    private static string CellText(IRenderedComponent<DrawioEditor> cut, Guid missionId, string column) =>
        cut.Find($"#drawio-mission-list-table tr[data-mission-id='{missionId}'] td[data-column='{column}']").TextContent.Trim();

    private static string Position(IRenderedComponent<DrawioEditor> cut, Guid missionId) =>
        CellText(cut, missionId, nameof(MissionDto.PositionItemId));

    private static string MoveOutcome(IRenderedComponent<DrawioEditor> cut) =>
        cut.Find("#drawio-mission-move-outcome").TextContent;

    private static void Choose(IRenderedComponent<DrawioEditor> cut, Guid missionId) =>
        Act(cut, () => cut.Find($"#drawio-mission-list-table tr[data-mission-id='{missionId}']").Click());

    private static void PickTarget(IRenderedComponent<DrawioEditor> cut, Guid target) =>
        Act(cut, () => cut.Find("#drawio-mission-move-target").Change(target.ToString()));

    private static void AskAndConfirm(IRenderedComponent<DrawioEditor> cut)
    {
        Act(cut, () => cut.Find("#drawio-mission-move-button").Click());
        Act(cut, () => cut.Find("#drawio-mission-move-confirm").Click());
    }

    private static string Destination(IRenderedComponent<DrawioEditor> cut, Guid missionId) =>
        CellText(cut, missionId, nameof(MissionDto.DestinationItemId));

    private static string Gateway(IRenderedComponent<DrawioEditor> cut, Guid missionId) =>
        CellText(cut, missionId, nameof(MissionDto.GatewayItemId));

    private static void AskToSetDestination(IRenderedComponent<DrawioEditor> cut) =>
        Act(cut, () => cut.Find("#drawio-mission-destination-button").Click());

    private static void ConfirmDestination(IRenderedComponent<DrawioEditor> cut) =>
        Act(cut, () => cut.Find("#drawio-mission-destination-confirm").Click());

    /// <summary>One of the gateway's radio buttons in the question: keep, clear, change - or none, add.</summary>
    private static void ChooseGateway(IRenderedComponent<DrawioEditor> cut, string option) =>
        Act(cut, () => cut.Find($"#drawio-mission-destination-gateway-{option}").Change(true));

    private static void PickGateway(IRenderedComponent<DrawioEditor> cut, Guid gateway) =>
        Act(cut, () => cut.Find("#drawio-mission-destination-gateway-target").Change(gateway.ToString()));

    /// <summary>What the page asked the host for: the mission, the destination, the gateway's mode and device.</summary>
    private static (Guid, Guid, MissionGatewayUpdateMode?, Guid?) Asked((Guid MissionId, MissionDestinationUpdate Update) request) =>
        (request.MissionId, request.Update.ItemId, request.Update.Gateway?.Mode, request.Update.Gateway?.ItemId);

    // ---- Reading ----------------------------------------------------------------------------------

    /// <summary>
    /// Shut, nothing is read; opened, the active missions are read with ONE request, and the Guid
    /// columns name the devices - except the three the WinForms grid left as they are.
    /// </summary>
    [Fact]
    public void OpeningTheListReadsTheActiveMissionsOnceWithEveryColumn()
    {
        var cut = RenderPage();
        Assert.Equal(0, MissionReads);

        OpenTheList(cut);

        Assert.Equal(1, MissionReads);
        Assert.Equal("Shuttle 1", Position(cut, M1));
        Assert.Equal("Shuttle 2", Position(cut, M2));
        Assert.Equal("LU-001", CellText(cut, M1, nameof(MissionDto.LoadUnitCode)));
        Assert.Equal(M1.ToString(), CellText(cut, M1, nameof(MissionDto.MissionId)));
        Assert.Empty(cut.FindAll("#drawio-mission-list-table input, #drawio-mission-list-table select"));
    }

    /// <summary>
    /// The default view carries exactly the ten operational columns chosen by the owner, in their
    /// chosen order. Extended is not a second partial list: it contains every field the current
    /// MissionDto exposes, in the contract's order.
    /// </summary>
    [Fact]
    public void CompactHasTheTenOperationalColumnsAndExtendedHasEveryCurrentField()
    {
        var cut = RenderPage();
        OpenTheList(cut);

        var compact = cut.FindAll("#drawio-mission-list-table thead th:not([hidden])")
            .Select(th => th.GetAttribute("data-column"));
        Assert.Equal(CompactColumns, compact);
        Assert.Equal("true", cut.Find("#drawio-mission-list-view-compact").GetAttribute("aria-pressed"));

        Act(cut, () => cut.Find("#drawio-mission-list-view-extended").Click());

        var extended = cut.FindAll("#drawio-mission-list-table thead th:not([hidden])")
            .Select(th => th.GetAttribute("data-column"));
        Assert.Equal(typeof(MissionDto).GetConstructors().Single().GetParameters().Select(parameter => parameter.Name), extended);
        Assert.Equal("true", cut.Find("#drawio-mission-list-view-extended").GetAttribute("aria-pressed"));
    }

    /// <summary>The filter's search affordance is inside its field, not detached above it.</summary>
    [Fact]
    public void TheMissionFilterUsesTheSearchIconInsideItsField()
    {
        var cut = RenderPage();
        OpenTheList(cut);

        var filter = cut.Find("#drawio-mission-list-filter");
        Assert.Equal("search", filter.ParentElement!.QuerySelector(".drawio-icon")!.GetAttribute("data-icon"));
        Assert.Contains("drawio-filter", filter.ParentElement.ClassList);
        Assert.Equal("Filter missions", filter.GetAttribute("aria-label"));
    }

    /// <summary>
    /// The filter says what it does, in the page's words (an LU, not an "UDC"): it looks at every field
    /// of a mission - in Compact too, where the mission's own identifier is not shown and still finds it.
    /// </summary>
    [Fact]
    public void TheMissionFilterSaysItLooksAtEveryFieldAndDoesInCompactToo()
    {
        var cut = RenderPage();
        OpenTheList(cut);

        Assert.Equal("Filter on any field: LU, source, destination, status...",
            cut.Find("#drawio-mission-list-filter").GetAttribute("placeholder"));
        Assert.Equal("true", cut.Find("#drawio-mission-list-view-compact").GetAttribute("aria-pressed"));
        Assert.DoesNotContain(nameof(MissionDto.MissionId), CompactColumns);

        Act(cut, () => cut.Find("#drawio-mission-list-filter").Input(M2.ToString()));

        Assert.Equal([M2.ToString()], Rows(cut));
    }

    [Theory]
    [InlineData(MissionStatusTypes.Run, "drawio-mission-status-run")]
    [InlineData(MissionStatusTypes.Stop, "drawio-mission-status-stop")]
    [InlineData(MissionStatusTypes.Done, "drawio-mission-status-done")]
    [InlineData(MissionStatusTypes.Error, "drawio-mission-status-error")]
    public void MissionStatusesHaveTheirOwnSemanticTone(MissionStatusTypes status, string expectedClass)
    {
        _host.Missions = [TestMissions.Mission(M1, A, status: status)];
        var cut = RenderPage();
        OpenTheList(cut);

        var cell = cut.Find($"#drawio-mission-list-table td[data-column='{nameof(MissionDto.Status)}']");
        Assert.Contains(expectedClass, cell.ClassList);
        Assert.DoesNotContain(expectedClass, cut.Find($"#drawio-mission-list-table th[data-column='{nameof(MissionDto.Status)}']").ClassList);
    }

    /// <summary>The two different operator intents use explicit names and separate functional colours.</summary>
    [Fact]
    public void AChosenMissionOffersTwoClearlySeparatedActions()
    {
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);

        var position = cut.Find("#drawio-mission-move-button");
        var destination = cut.Find("#drawio-mission-destination-button");
        Assert.Equal("Change position", position.TextContent.Trim());
        Assert.Contains("drawio-mission-action-position", position.ClassList);
        Assert.Equal("Send elsewhere", destination.TextContent.Trim());
        Assert.Contains("drawio-mission-action-destination", destination.ClassList);
    }

    /// <summary>The grid shows ten missions at a time and its footer changes pages without resizing the window.</summary>
    [Fact]
    public void TheGridPaginatesTheActiveMissionsTenAtATime()
    {
        _host.Missions = Enumerable.Range(1, 23)
            .Select(i => TestMissions.Mission(NumberedMission(i), A, $"LU-{i:000}", i))
            .ToList();
        var cut = RenderPage();
        OpenTheList(cut);

        Assert.Equal(Enumerable.Range(1, 10).Select(i => NumberedMission(i).ToString()), Rows(cut));
        Assert.Equal("1–10 of 23", cut.Find("#drawio-mission-list-page-summary").TextContent.Trim());
        Assert.True(cut.Find("#drawio-mission-list-previous").HasAttribute("disabled"));

        Act(cut, () => cut.Find("#drawio-mission-list-next").Click());

        Assert.Equal(Enumerable.Range(11, 10).Select(i => NumberedMission(i).ToString()), Rows(cut));
        Assert.Equal("11–20 of 23", cut.Find("#drawio-mission-list-page-summary").TextContent.Trim());

        Act(cut, () => cut.Find("#drawio-mission-list-next").Click());

        Assert.Equal(Enumerable.Range(21, 3).Select(i => NumberedMission(i).ToString()), Rows(cut));
        Assert.Equal("21–23 of 23", cut.Find("#drawio-mission-list-page-summary").TextContent.Trim());
        Assert.True(cut.Find("#drawio-mission-list-next").HasAttribute("disabled"));
    }

    /// <summary>Filtering applies to all missions and starts its result from the first page.</summary>
    [Fact]
    public void FilteringFromAnotherPageReturnsToTheFirstFilteredPage()
    {
        _host.Missions = Enumerable.Range(1, 23)
            .Select(i => TestMissions.Mission(NumberedMission(i), A, $"LU-{i:000}", i))
            .ToList();
        var cut = RenderPage();
        OpenTheList(cut);
        Act(cut, () => cut.Find("#drawio-mission-list-next").Click());

        Act(cut, () => cut.Find("#drawio-mission-list-filter").Input("LU-003"));

        Assert.Equal([NumberedMission(3).ToString()], Rows(cut));
        Assert.Equal("1 of 1", cut.Find("#drawio-mission-list-page-summary").TextContent.Trim());
        Assert.True(cut.Find("#drawio-mission-list-previous").HasAttribute("disabled"));
        Assert.True(cut.Find("#drawio-mission-list-next").HasAttribute("disabled"));
    }

    private static Guid NumberedMission(int number) =>
        Guid.Parse($"00000000-0000-4000-8000-{number:000000000000}");

    [Fact]
    public void CompactDoesNotKeepAnInvisibleExtendedSort()
    {
        var cut = RenderPage();
        OpenTheList(cut);
        Act(cut, () => cut.Find("#drawio-mission-list-view-extended").Click());
        Act(cut, () => cut.Find($"#drawio-mission-list-table th[data-column='{nameof(MissionDto.MissionId)}']").Click());

        Act(cut, () => cut.Find("#drawio-mission-list-view-compact").Click());

        Assert.All(cut.FindAll("#drawio-mission-list-table thead th"),
            header => Assert.Equal("none", header.GetAttribute("aria-sort")));
    }

    [Fact]
    public void APageClampedByLiveCompletionsDoesNotJumpBackWhenMissionsArrive()
    {
        _host.Missions = Enumerable.Range(1, 23)
            .Select(i => TestMissions.Mission(NumberedMission(i), A, $"LU-{i:000}", i))
            .ToList();
        var cut = RenderPage();
        OpenTheList(cut);
        Act(cut, () => cut.Find("#drawio-mission-list-next").Click());
        Act(cut, () => cut.Find("#drawio-mission-list-next").Click());

        foreach (var i in Enumerable.Range(6, 18))
            _source.RaiseMission(TestMissions.Mission(NumberedMission(i), A), MissionChange.Completed);
        cut.WaitForAssertion(() => Assert.Equal("1–5 of 5", cut.Find("#drawio-mission-list-page-summary").TextContent.Trim()));

        foreach (var i in Enumerable.Range(24, 17))
            _source.RaiseMission(TestMissions.Mission(NumberedMission(i), A, $"LU-{i:000}", i), MissionChange.Started);

        cut.WaitForAssertion(() => Assert.Equal("1–10 of 22", cut.Find("#drawio-mission-list-page-summary").TextContent.Trim()));
    }

    /// <summary>What the channel says moves the rows, without reading the list again: a mission moved, one ended, one started.</summary>
    [Fact]
    public void TheChannelKeepsTheRowsWithoutAnotherRead()
    {
        var cut = RenderPage();
        OpenTheList(cut);

        _source.RaiseMission(TestMissions.Mission(M1, C, "LU-001"), MissionChange.PositionChanged);
        _source.RaiseMission(TestMissions.Mission(M2, B, "LU-002"), MissionChange.Completed);
        _source.RaiseMission(TestMissions.Mission(M3, A, "LU-003"), MissionChange.Started);

        cut.WaitForAssertion(() => Assert.Equal([M1.ToString(), M3.ToString()], Rows(cut).Order()));
        Assert.Equal("Lift", Position(cut, M1));
        Assert.Equal(1, MissionReads);
    }

    /// <summary>
    /// The read in flight may predate what the channel says meanwhile: the host answers with the
    /// mission where it WAS, the channel has already said it moved - the row shows where it is.
    /// </summary>
    [Fact]
    public async Task AnEventWhileTheListIsBeingReadGoesOnTopOfTheAnswer()
    {
        var cut = RenderPage();
        _host.MissionsGate = new TaskCompletionSource();

        Act(cut, () => cut.Find("#drawio-mission-list-toggle").Click());
        await _host.MissionsGateReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _source.RaiseMission(TestMissions.Mission(M1, C, "LU-001"), MissionChange.PositionChanged);
        // The event has reached the list (a render of its own, with nothing to show yet) before the answer is let go.
        await cut.InvokeAsync(() => { });
        _host.MissionsGate.SetResult();

        cut.WaitForAssertion(() => Assert.Equal(2, Rows(cut).Count));
        Assert.Equal("Lift", Position(cut, M1));
    }

    /// <summary>Events that went by while the channel was down do not come back: at the reconnection the list is read again.</summary>
    [Fact]
    public void AReconnectionReadsTheListAgain()
    {
        var cut = RenderPage();
        OpenTheList(cut);

        _source.RaiseReconnecting();
        _host.Missions = [TestMissions.Mission(M1, C, "LU-001")];
        _source.RaiseReconnected();

        cut.WaitForAssertion(() => Assert.Equal([M1.ToString()], Rows(cut)));
        Assert.Equal("Lift", Position(cut, M1));
        Assert.Equal(2, MissionReads);
    }

    /// <summary>
    /// Shut, the list neither reads nor renders what the channel says; it only knows it is stale,
    /// and reads again when it is opened. Opened with nothing gone by, it is not read again.
    /// </summary>
    [Fact]
    public async Task ShutTheListOnlyNotesThatItIsStaleAndReadsAgainWhenOpened()
    {
        var cut = RenderPage();
        OpenTheList(cut);
        Act(cut, () => cut.Find("#drawio-mission-list-toggle").Click());
        Act(cut, () => cut.Find("#drawio-mission-list-toggle").Click());
        Assert.Equal(1, MissionReads);
        Act(cut, () => cut.Find("#drawio-mission-list-toggle").Click());

        _host.Missions = [TestMissions.Mission(M2, C, "LU-002")];
        _source.RaiseMission(TestMissions.Mission(M1, A, "LU-001"), MissionChange.Completed);
        await cut.InvokeAsync(() => { });
        Assert.Equal(1, MissionReads);
        Assert.Empty(Rows(cut));

        Act(cut, () => cut.Find("#drawio-mission-list-toggle").Click());

        cut.WaitForAssertion(() => Assert.Equal([M2.ToString()], Rows(cut)));
        Assert.Equal(2, MissionReads);
    }

    // ---- Choosing a row ---------------------------------------------------------------------------

    /// <summary>
    /// A row chosen while the drawing does not answer: why in the page's words, and what the page's
    /// script threw behind the line's "?" (row 37 of the queue, extended to the whole page on 10/10).
    /// </summary>
    [Fact]
    public async Task AGoToTheDrawingDoesNotAnswerKeepsTheFailureBehindTheQuestionMark()
    {
        var cut = RenderPage();
        _bridge.Setup<string?>("selectCell", _ => true).SetException(new TaskCanceledException("selectCell senza risposta (prova)"));
        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());
        OpenTheList(cut);

        Choose(cut, M2);

        cut.WaitForAssertion(() => Assert.Equal("Shuttle 2: the drawing did not answer, because the page's script did not answer in time, so nothing was selected.",
            cut.Find("#drawio-mission-list-goto").TextContent));
        Assert.Equal("TaskCanceledException: selectCell senza risposta (prova)", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-mission-list-goto"));
    }

    /// <summary>A row chosen selects on the drawing the device the mission is on, and is marked as the mission to move.</summary>
    [Fact]
    public async Task ChoosingARowSelectsTheDeviceTheMissionIsOn()
    {
        var cut = RenderPage();
        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());
        OpenTheList(cut);

        Choose(cut, M2);

        cut.WaitForAssertion(() => Assert.Contains(_bridge.Invocations["selectCell"], call => (call.Arguments[0] as string) == B.ToString()));
        cut.WaitForAssertion(() => Assert.Contains("Shuttle 2 is selected in the drawing", cut.Find("#drawio-mission-list-goto").TextContent));
        Assert.Equal("true", cut.Find($"#drawio-mission-list-table tr[data-mission-id='{M2}']").GetAttribute("aria-selected"));
        Assert.Contains("the mission of load unit LU-002, now on Shuttle 2", cut.Find("#drawio-mission-move-chosen").TextContent);
    }

    // ---- Moving -----------------------------------------------------------------------------------

    /// <summary>
    /// Two steps: "Move" only asks, naming the mission, where it is and where it goes; "Yes" moves it
    /// on the host - without asking for the edit permit, which the host does not require of this
    /// route - and the row then shows where the HOST says the mission is.
    /// </summary>
    [Fact]
    public void AMoveIsConfirmedInTwoStepsAndTheRowFollowsTheHost()
    {
        var cut = RenderPage();
        OpenTheList(cut);
        var permits = _host.EditLockRequests;
        Choose(cut, M1);
        PickTarget(cut, C);

        Act(cut, () => cut.Find("#drawio-mission-move-button").Click());
        Assert.Equal("Move the mission of load unit LU-001 from Shuttle 1 to Lift?", cut.Find("#drawio-mission-move-question").TextContent);
        Assert.Empty(_host.MissionMoves);

        Act(cut, () => cut.Find("#drawio-mission-move-confirm").Click());

        cut.WaitForAssertion(() => Assert.Contains("The mission of load unit LU-001 was moved onto Lift: the host took it.", MoveOutcome(cut)));
        Assert.Equal((M1, C), Assert.Single(_host.MissionMoves));
        cut.WaitForAssertion(() => Assert.Equal("Lift", Position(cut, M1)));
        Assert.Equal(permits, _host.EditLockRequests);
    }

    /// <summary>Never optimistic: while the host has not answered, the row stays where the mission was.</summary>
    [Fact]
    public async Task TheRowDoesNotMoveBeforeTheHostAnswers()
    {
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, C);
        _host.MissionMoveGate = new TaskCompletionSource();

        AskAndConfirm(cut);
        await _host.MissionMoveGateReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#drawio-mission-move-sending")));
        Assert.Equal("Shuttle 1", Position(cut, M1));

        _host.MissionMoveGate.SetResult();
        cut.WaitForAssertion(() => Assert.Equal("Lift", Position(cut, M1)));
    }

    /// <summary>
    /// A device that carries another mission: the host refuses, the row stays, and the line says what a
    /// 409 means on this route (since item 54 only the device occupied: a step of the mission under way
    /// makes the move wait); the host's own words are behind its "?" (row 37 of the queue, extended to
    /// the whole page on 10/10).
    /// </summary>
    [Fact]
    public void AMoveOntoAnOccupiedDeviceIsRefusedAndTheRowStays()
    {
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, B);

        AskAndConfirm(cut);

        cut.WaitForAssertion(() => Assert.Contains("was NOT moved onto Shuttle 2", MoveOutcome(cut)));
        Assert.Equal("The mission of load unit LU-001 was NOT moved onto Shuttle 2: the host refused it - Shuttle 2 carries another mission. It is still on Shuttle 1.",
            MoveOutcome(cut));
        var answer = DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-mission-move-outcome");
        Assert.StartsWith("the host answered 409: {", answer);
        Assert.Contains("Il dispositivo " + B + " porta gi", answer);
        Assert.Empty(_host.MissionMoves);
        Assert.Equal("Shuttle 1", Position(cut, M1));
        Assert.Equal(1, MissionReads);
    }

    /// <summary>
    /// Item 54, decision 4: a mission with a step in flight is not refused any more. The host answers 202,
    /// the line says the move is applied at the end of the movement, the list is read again, and the row
    /// and the chosen mission's line show the move that waits - the mission still where it is.
    /// </summary>
    [Fact]
    public void AMoveDuringAStepIsAppliedAtTheEndOfTheMovementAndTheRowShowsIt()
    {
        var cut = RenderPage();
        OpenTheList(cut);
        _host.MissionsWithAStepInFlight.Add(M1);
        Choose(cut, M1);
        PickTarget(cut, C);

        AskAndConfirm(cut);

        cut.WaitForAssertion(() => Assert.Equal(
            "The mission of load unit LU-001 has a step under way: the move onto Lift is applied at the end of the movement.",
            MoveOutcome(cut)));
        Assert.Empty(_host.MissionMoves);
        Assert.Equal(2, MissionReads);
        Assert.Equal("Shuttle 1", Position(cut, M1));
        Assert.Equal("Position", CellText(cut, M1, nameof(MissionDto.PendingChange)));
        Assert.Equal("Lift", CellText(cut, M1, nameof(MissionDto.PendingChangeItemId)));
        Assert.EndsWith("At the end of its movement it is moved onto Lift.",
            cut.Find("#drawio-mission-move-chosen").TextContent.Trim());
    }

    /// <summary>
    /// Item 54, decision 4: the same for a change of destination - 202, "applied at the end of the
    /// movement", the list read again, and the row and the chosen mission's line show the change that
    /// waits, with the destination still the one it had.
    /// </summary>
    [Fact]
    public void AChangeOfDestinationDuringAStepIsAppliedAtTheEndOfTheMovementAndTheRowShowsIt()
    {
        _host.Junctions = [Junction(A, C)];
        _host.MissionsWithAStepInFlight.Add(M1);
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, C);

        AskToSetDestination(cut);
        ConfirmDestination(cut);

        cut.WaitForAssertion(() => Assert.Equal(
            "LU-001 is moving: it is sent to Lift at the end of the movement - the change is applied at the end of the movement.",
            MoveOutcome(cut)));
        Assert.Equal(2, MissionReads);
        Assert.Equal("Destination", CellText(cut, M1, nameof(MissionDto.PendingChange)));
        Assert.Equal("Lift", CellText(cut, M1, nameof(MissionDto.PendingChangeItemId)));
        Assert.Equal("", Destination(cut, M1));
        Assert.EndsWith("At the end of its movement it is sent to Lift.",
            cut.Find("#drawio-mission-move-chosen").TextContent.Trim());
    }

    /// <summary>
    /// Owner's answer 4 of 8/10: with the engine still in Recover the host refuses the move with a 503
    /// and its own title, and the page says so in plain words - a refusal, not an outcome "not known":
    /// nothing moved, and the list is not read again.
    /// </summary>
    [Fact]
    public void WhileTheEngineIsStillLoadingTheMoveIsRefusedAndSaidSo()
    {
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, C);
        _host.NavStatus = NavigatorStatusTypes.Recover;

        AskAndConfirm(cut);

        cut.WaitForAssertion(() => Assert.Equal(
            "The mission of load unit LU-001 was NOT moved onto Lift. The engine is still loading the missions: try again in a moment.",
            MoveOutcome(cut)));
        // The host's answer behind the line's "?" (row 37 of the queue, extended on 10/10).
        Assert.StartsWith("the host answered 503: {", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-mission-move-outcome"));
        Assert.Empty(_host.MissionMoves);
        Assert.Equal("Shuttle 1", Position(cut, M1));
        Assert.Equal(1, MissionReads);
    }

    /// <summary>
    /// Row 34 of the queue: the 503 of the engine not loaded - the host's own problem document, with the
    /// field that says which 503 it is - is a refusal before anything moved, as the Recover one is: the
    /// host's filter turned the request away before it reached the engine. The page says so in plain
    /// words, and does not read the list again (it would get the same 503).
    /// </summary>
    [Fact]
    public void WhileTheEngineIsNotLoadedTheMoveIsRefusedAndSaidSo()
    {
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, C);
        _host.FailRoute = "mission-position";
        _host.FailureStatus = HttpStatusCode.ServiceUnavailable;
        _host.FailureBody = EngineNotLoadedAnswer;

        AskAndConfirm(cut);

        cut.WaitForAssertion(() => Assert.Equal(
            "The mission of load unit LU-001 was NOT moved onto Lift: the engine is not ready (it is not loaded on the host). It is still on Shuttle 1.",
            MoveOutcome(cut)));
        var answer = DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-mission-move-outcome");
        Assert.StartsWith("the host answered 503: {", answer);
        Assert.Contains("Il grafo non si carica", answer);
        Assert.Empty(_host.MissionMoves);
        Assert.Equal("Shuttle 1", Position(cut, M1));
        Assert.Equal(1, MissionReads);
    }

    /// <summary>The 503 of the engine not loaded as the host writes it (EngineExceptionHandler), with the field of row 34 of the queue.</summary>
    private const string EngineNotLoadedAnswer =
        "{\"type\":\"https://tools.ietf.org/html/rfc9110#section-15.6.4\",\"title\":\"Il motore non \u00e8 caricato\"," +
        "\"status\":503,\"detail\":\"Il grafo non si carica\",\"unavailable\":\"engineNotLoaded\"}";

    /// <summary>
    /// The other 503s of the same route that do not say which they are - the engine without its
    /// database, or an answer with no body - carry no field and not the Recover title: the page cannot
    /// tell whether the mission moved, says so, and reads the list again. The title is what tells the
    /// Recover refusal from them, the field (row 34 of the queue) the engine not loaded.
    /// </summary>
    [Fact]
    public void AnotherServiceUnavailableIsNotTakenForTheRecoverRefusal()
    {
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, C);
        _host.FailRoute = "mission-position";
        _host.FailureStatus = HttpStatusCode.ServiceUnavailable;

        AskAndConfirm(cut);

        cut.WaitForAssertion(() => Assert.Contains("It is NOT known whether the mission of load unit LU-001 was moved onto Lift", MoveOutcome(cut)));
        Assert.DoesNotContain("still loading the missions", MoveOutcome(cut));
        cut.WaitForAssertion(() => Assert.Equal(2, MissionReads));
    }

    /// <summary>A mission that ended meanwhile: nothing moved, and the list is read again - its row goes.</summary>
    [Fact]
    public void AMissionThatEndedMeanwhileIsSaidSoAndItsRowGoes()
    {
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, C);
        _host.Missions.RemoveAll(m => m.MissionId == M1);

        AskAndConfirm(cut);

        cut.WaitForAssertion(() => Assert.Contains("was NOT moved: it is no longer active on the host", MoveOutcome(cut)));
        Assert.StartsWith("the host answered 404", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-mission-move-outcome"));
        cut.WaitForAssertion(() => Assert.Equal([M2.ToString()], Rows(cut)));
    }

    /// <summary>An answer that never came: whether it moved is not known, said so, and the list is read again from the host.</summary>
    [Fact]
    public void AMoveWhoseAnswerIsLostIsNotKnownAndTheListIsReadAgain()
    {
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, C);
        _host.HttpFailureRoute = "mission-position";

        AskAndConfirm(cut);

        cut.WaitForAssertion(() => Assert.Contains("It is NOT known whether the mission of load unit LU-001 was moved onto Lift", MoveOutcome(cut)));
        cut.WaitForAssertion(() => Assert.Equal(2, MissionReads));
    }

    /// <summary>The target taken from the drawing: the device selected there, like Test Route's endpoints.</summary>
    [Fact]
    public async Task TheTargetCanBeTheDeviceSelectedOnTheDrawing()
    {
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([C.ToString()]));
        cut.WaitForAssertion(() => Assert.False(cut.Find("#drawio-mission-move-use-selected").HasAttribute("disabled")));

        Act(cut, () => cut.Find("#drawio-mission-move-use-selected").Click());

        Assert.Equal("Target: Lift", cut.Find("#drawio-mission-move-target-chosen").TextContent.Trim());
        AskAndConfirm(cut);
        cut.WaitForAssertion(() => Assert.Equal((M1, C), Assert.Single(_host.MissionMoves)));
    }

    /// <summary>The search narrows the devices offered as the target, by name.</summary>
    [Fact]
    public void TheSearchNarrowsTheTargets()
    {
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);

        Act(cut, () => cut.Find("#drawio-mission-move-search").Input("shut"));

        var offered = cut.FindAll("#drawio-mission-move-target option").Select(o => o.GetAttribute("value")).ToList();
        Assert.Equal(["", A.ToString(), B.ToString()], offered);
    }

    /// <summary>A move onto where the mission already is, or without a target, cannot be asked.</summary>
    [Fact]
    public void AMoveOntoWhereTheMissionIsCannotBeAsked()
    {
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        Assert.True(cut.Find("#drawio-mission-move-button").HasAttribute("disabled"));

        PickTarget(cut, A);

        Assert.True(cut.Find("#drawio-mission-move-button").HasAttribute("disabled"));
    }

    // ---- Fix round 1 ------------------------------------------------------------------------------

    /// <summary>
    /// Opened, shut while its read is still on its way, told meanwhile that a mission ended, and opened
    /// again before the answer lands: the answer predates the event, so the list reads again after it
    /// and the mission is gone.
    /// </summary>
    [Fact]
    public async Task AnEventWhileShutDuringAReadIsNotLostWhenTheListIsOpenedAgain()
    {
        var cut = RenderPage();
        _host.MissionsGate = new TaskCompletionSource();
        Act(cut, () => cut.Find("#drawio-mission-list-toggle").Click());
        await _host.MissionsGateReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Act(cut, () => cut.Find("#drawio-mission-list-toggle").Click());

        _host.Missions.RemoveAll(m => m.MissionId == M2);
        _source.RaiseMission(TestMissions.Mission(M2, B, "LU-002"), MissionChange.Completed);
        await cut.InvokeAsync(() => { });
        Act(cut, () => cut.Find("#drawio-mission-list-toggle").Click());
        _host.MissionsGate.SetResult();

        cut.WaitForAssertion(() => Assert.Equal([M1.ToString()], Rows(cut)));
        Assert.Equal(2, MissionReads);
    }

    /// <summary>The same with a reconnection instead of an event: the resync that came while shut is not lost either.</summary>
    [Fact]
    public async Task AResyncWhileShutDuringAReadIsNotLostWhenTheListIsOpenedAgain()
    {
        var cut = RenderPage();
        _host.MissionsGate = new TaskCompletionSource();
        Act(cut, () => cut.Find("#drawio-mission-list-toggle").Click());
        await _host.MissionsGateReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Act(cut, () => cut.Find("#drawio-mission-list-toggle").Click());

        _source.RaiseReconnecting();
        _host.Missions = [TestMissions.Mission(M1, C, "LU-001")];
        _source.RaiseReconnected();
        await cut.InvokeAsync(() => { });
        Act(cut, () => cut.Find("#drawio-mission-list-toggle").Click());
        _host.MissionsGate.SetResult();

        cut.WaitForAssertion(() => Assert.Equal([M1.ToString()], Rows(cut)));
        Assert.Equal("Lift", Position(cut, M1));
    }

    /// <summary>A failure of a kind the page does not name: the move's outcome is NOT known, said so, and the list is read again - nothing escapes the handler.</summary>
    [Fact]
    public void AnUnforeseenFailureOfTheMoveIsNotKnownAndTheListIsReadAgain()
    {
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, C);
        _host.MissionMoveFailure = new InvalidOperationException("something nobody expected (test)");

        AskAndConfirm(cut);

        cut.WaitForAssertion(() => Assert.Equal(
            "It is NOT known whether the mission of load unit LU-001 was moved onto Lift, because an unforeseen failure stopped it. The list is read again from the host.",
            MoveOutcome(cut)));
        Assert.Equal("InvalidOperationException: something nobody expected (test)",
            DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-mission-move-outcome"));
        cut.WaitForAssertion(() => Assert.Equal(2, MissionReads));
    }

    /// <summary>A 500 is not a refusal: whether the mission moved is NOT known, and the list is read again.</summary>
    [Fact]
    public void AMoveAnsweredWithAServerErrorIsNotKnownAndTheListIsReadAgain()
    {
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, C);
        _host.FailRoute = "mission-position";
        _host.FailureStatus = HttpStatusCode.InternalServerError;

        AskAndConfirm(cut);

        cut.WaitForAssertion(() => Assert.Contains("It is NOT known whether the mission of load unit LU-001 was moved onto Lift", MoveOutcome(cut)));
        cut.WaitForAssertion(() => Assert.Equal(2, MissionReads));
    }

    /// <summary>A device the host no longer knows (400): refused, the host's words behind the line's "?", the row as it was, no reread.</summary>
    [Fact]
    public void AMoveOntoADeviceTheHostDoesNotKnowIsRefusedAndTheRowStays()
    {
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, C);
        _host.Items.RemoveAll(item => item.ItemId == C);

        AskAndConfirm(cut);

        cut.WaitForAssertion(() => Assert.Contains("was NOT moved onto Lift: the host does not know Lift as a device", MoveOutcome(cut)));
        Assert.Equal("The mission of load unit LU-001 was NOT moved onto Lift: the host does not know Lift as a device. It is still on Shuttle 1.", MoveOutcome(cut));
        Assert.Contains("Il dispositivo " + C + " non esiste", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-mission-move-outcome"));
        Assert.Equal("Shuttle 1", Position(cut, M1));
        Assert.Equal(1, MissionReads);
    }

    // ---- Setting the destination (missions plan of 30/9, task 6) -----------------------------------

    /// <summary>
    /// "Set destination" sits beside Move and takes the same target. Two steps: it only asks - the load
    /// unit by its code, the new destination and the one it replaces, with ONE blue button - and the
    /// blue "Set destination" sends it, without the edit permit; the outcome is one line, and the row
    /// then reads the new destination from the host.
    /// </summary>
    [Fact]
    public void SetDestinationIsAskedBesideMoveOnTheSameTargetAndTheRowReadsTheNewDestination()
    {
        _host.Missions = [TestMissions.Mission(M1, A, "LU-001", destination: B), TestMissions.Mission(M2, B, "LU-002")];
        _host.Junctions = [Junction(A, C)];
        var cut = RenderPage();
        OpenTheList(cut);
        var permits = _host.EditLockRequests;
        Choose(cut, M1);
        PickTarget(cut, C);
        Assert.False(cut.Find("#drawio-mission-move-button").HasAttribute("disabled"));
        Assert.False(cut.Find("#drawio-mission-destination-button").HasAttribute("disabled"));

        AskToSetDestination(cut);

        Assert.Equal("Send LU-001 to Lift instead of Shuttle 2?", cut.Find("#drawio-mission-destination-question").TextContent);
        Assert.Equal("drawio-mission-destination-confirm", Assert.Single(cut.FindAll("#drawio-mission-move .drawio-btn-primary")).Id);
        Assert.Empty(cut.FindAll("#drawio-mission-move-button"));
        Assert.Empty(_host.MissionDestinationRequests);

        ConfirmDestination(cut);

        cut.WaitForAssertion(() => Assert.Equal("LU-001 is now bound for Lift: the host took it.", MoveOutcome(cut)));
        Assert.Equal((M1, C, MissionGatewayUpdateMode.Keep, (Guid?)null), Asked(Assert.Single(_host.MissionDestinationRequests)));
        cut.WaitForAssertion(() => Assert.Equal("Lift", Destination(cut, M1)));
        Assert.Equal(2, MissionReads);
        Assert.Equal(permits, _host.EditLockRequests);
    }

    /// <summary>
    /// Never optimistic: while the host has not answered, the row keeps the destination it had
    /// (Shuttle 2, not an empty cell that an optimistic write could look like).
    /// </summary>
    [Fact]
    public async Task TheDestinationDoesNotChangeOnTheRowBeforeTheHostAnswers()
    {
        _host.Missions = [TestMissions.Mission(M1, A, "LU-001", destination: B), TestMissions.Mission(M2, B, "LU-002")];
        _host.Junctions = [Junction(A, C)];
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, C);
        _host.MissionDestinationGate = new TaskCompletionSource();

        AskToSetDestination(cut);
        ConfirmDestination(cut);
        await _host.MissionDestinationGateReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        cut.WaitForAssertion(() => Assert.Equal("Asking the host to send LU-001 to Lift...", cut.Find("#drawio-mission-move-sending").TextContent));
        Assert.Equal("Shuttle 2", Destination(cut, M1));

        _host.MissionDestinationGate.SetResult();
        cut.WaitForAssertion(() => Assert.Equal("Lift", Destination(cut, M1)));
        Assert.Empty(cut.FindAll("#drawio-mission-move-sending"));
    }

    /// <summary>
    /// The owner's decision on the gateway (30/9, evening): the question always shows the mission's
    /// gateway, with "keep" chosen to begin with, and sends what the operator chose - keep it, clear
    /// it, or change it for a device found with the same search as the target.
    /// </summary>
    [Theory]
    [InlineData("keep")]
    [InlineData("clear")]
    [InlineData("change")]
    public void WithAGatewayTheQuestionKeepsItUnlessTheOperatorSaysOtherwise(string option)
    {
        _host.Items.Add(Item(D, "Conveyor", 900));
        _host.Missions = [TestMissions.Mission(M1, A, "LU-001") with { GatewayItemId = B }, TestMissions.Mission(M2, B, "LU-002")];
        _host.Junctions = [Junction(A, B), Junction(B, C), Junction(A, D), Junction(D, C), Junction(A, C)];
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, C);
        AskToSetDestination(cut);

        Assert.Equal("Gateway: Shuttle 2", cut.Find("#drawio-mission-destination-gateway-now").TextContent);
        Assert.Equal(["drawio-mission-destination-gateway-keep", "drawio-mission-destination-gateway-clear", "drawio-mission-destination-gateway-change"],
            cut.FindAll("#drawio-mission-destination-gateway input[type='radio']").Select(radio => radio.Id));
        Assert.True(cut.Find("#drawio-mission-destination-gateway-keep").HasAttribute("checked"));
        Assert.Empty(cut.FindAll("#drawio-mission-destination-gateway-target"));

        ChooseGateway(cut, option);
        if (option == "change")
        {
            Assert.True(cut.Find("#drawio-mission-destination-confirm").HasAttribute("disabled"));
            Act(cut, () => cut.Find("#drawio-mission-destination-gateway-search").Input("conv"));
            Assert.Equal(["", D.ToString()], cut.FindAll("#drawio-mission-destination-gateway-target option").Select(o => o.GetAttribute("value")));
            PickGateway(cut, D);
        }

        ConfirmDestination(cut);

        var (outcome, mode, device, gateway) = option switch
        {
            "keep" => ("LU-001 is now bound for Lift, still passing through Shuttle 2: the host took it.", MissionGatewayUpdateMode.Keep, (Guid?)null, "Shuttle 2"),
            "clear" => ("LU-001 is now bound for Lift, with no gateway: the host took it.", MissionGatewayUpdateMode.Clear, null, ""),
            _ => ("LU-001 is now bound for Lift, passing through Conveyor: the host took it.", MissionGatewayUpdateMode.Set, D, "Conveyor"),
        };
        cut.WaitForAssertion(() => Assert.Equal(outcome, MoveOutcome(cut)));
        Assert.Equal((M1, C, mode, device), Asked(Assert.Single(_host.MissionDestinationRequests)));
        cut.WaitForAssertion(() => Assert.Equal("Lift", Destination(cut, M1)));
        Assert.Equal(gateway, Gateway(cut, M1));
    }

    /// <summary>A mission without a gateway: the question says "none", chosen to begin with, and offers to add one.</summary>
    [Theory]
    [InlineData("none")]
    [InlineData("add")]
    public void WithoutAGatewayTheQuestionSaysNoneAndOffersToAddOne(string option)
    {
        _host.Junctions = [Junction(A, B), Junction(B, C), Junction(A, C)];
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, C);
        AskToSetDestination(cut);

        Assert.Equal("Send LU-001 to Lift?", cut.Find("#drawio-mission-destination-question").TextContent);
        Assert.Equal("Gateway: none", cut.Find("#drawio-mission-destination-gateway-now").TextContent);
        Assert.Equal(["drawio-mission-destination-gateway-none", "drawio-mission-destination-gateway-add"],
            cut.FindAll("#drawio-mission-destination-gateway input[type='radio']").Select(radio => radio.Id));
        Assert.True(cut.Find("#drawio-mission-destination-gateway-none").HasAttribute("checked"));

        ChooseGateway(cut, option);
        if (option == "add")
        {
            Act(cut, () => cut.Find("#drawio-mission-destination-gateway-search").Input("shuttle 2"));
            PickGateway(cut, B);
        }

        ConfirmDestination(cut);

        var (outcome, mode, device, gateway) = option == "none"
            ? ("LU-001 is now bound for Lift: the host took it.", MissionGatewayUpdateMode.Keep, (Guid?)null, "")
            : ("LU-001 is now bound for Lift, passing through Shuttle 2: the host took it.", MissionGatewayUpdateMode.Set, B, "Shuttle 2");
        cut.WaitForAssertion(() => Assert.Equal(outcome, MoveOutcome(cut)));
        Assert.Equal((M1, C, mode, device), Asked(Assert.Single(_host.MissionDestinationRequests)));
        cut.WaitForAssertion(() => Assert.Equal("Lift", Destination(cut, M1)));
        Assert.Equal(gateway, Gateway(cut, M1));
    }

    /// <summary>
    /// Every refusal in plain words, from the problem's title and fields - not the host's Italian - and
    /// the destination as it was. The 422s and the 503 are refusals too, taken BEFORE the arm that says
    /// "NOT known": nothing changed, and the list is not read again.
    /// </summary>
    [Theory]
    [InlineData("no route")]
    [InlineData("no route even without the gateway")]
    [InlineData("no free cell")]
    [InlineData("no other free cell")]
    [InlineData("no free cell for a destination of the configuration")]
    [InlineData("warehouse")]
    [InlineData("moving")]
    [InlineData("a copy moving")]
    [InlineData("cell search")]
    [InlineData("already there")]
    [InlineData("unknown destination")]
    [InlineData("engine not loaded")]
    public void EveryRefusalOfTheDestinationIsSaidInPlainWordsAndTheRowStays(string refusal)
    {
        _host.Items.Add(Item(S, "Warehouse 1", 900, DeviceTypes.Storage));
        var target = C;
        string expected;
        switch (refusal)
        {
            case "no route":
                expected = "LU-001 was NOT sent to Lift: there is no route from Shuttle 1 to Lift. It still has no destination.";
                break;
            case "no route even without the gateway":
                _host.Missions[0] = _host.Missions[0] with { GatewayItemId = B };
                expected = "LU-001 was NOT sent to Lift: there is no route from Shuttle 1 to Lift passing through Shuttle 2, nor without it. It still has no destination.";
                break;
            case "no free cell":
                target = S;
                _host.Junctions = [Junction(A, S)];
                _host.StoragesWithoutAFreeCell.Add(S);
                expected = "LU-001 was NOT sent to Warehouse 1: the warehouse system has no cell to give it in Warehouse 1 (the engine's log says why). It still has no destination.";
                break;
            case "no other free cell":
                target = S;
                // The engine excludes the cell the mission has only when its door is in the warehouse
                // chosen (MissionGateway.CellToReplaceAsync): without a door it asks for any cell (row 51),
                // and only when the operator chose the destination (the mark of row 44).
                _host.Missions[0] = _host.Missions[0] with { DestinationItemId = S, StorageDoorCode = "S01A", OperatorDestination = true };
                _host.DoorMappings.Add(new DoorMappingDto(1, S, "S01A", 1m, null, 0, false));
                _host.Junctions = [Junction(A, S)];
                _host.StoragesWithoutAFreeCell.Add(S);
                expected = "LU-001 was NOT sent to Warehouse 1: there is no other free cell in Warehouse 1 - the cell it has stays reserved for it. It is still bound for Warehouse 1.";
                break;
            case "no free cell for a destination of the configuration":
                target = S;
                // The same door in the same warehouse, but the destination was written by the configuration:
                // the engine excludes no cell (MissionGateway.CellToReplaceAsync), so it is any cell.
                _host.Missions[0] = _host.Missions[0] with { DestinationItemId = S, StorageDoorCode = "S01A", OperatorDestination = false };
                _host.DoorMappings.Add(new DoorMappingDto(1, S, "S01A", 1m, null, 0, false));
                _host.Junctions = [Junction(A, S)];
                _host.StoragesWithoutAFreeCell.Add(S);
                expected = "LU-001 was NOT sent to Warehouse 1: the warehouse system has no cell to give it in Warehouse 1 (the engine's log says why). It is still bound for Warehouse 1.";
                break;
            case "warehouse":
                target = S;
                _host.Junctions = [Junction(A, S)];
                _host.WarehouseUnreachable = true;
                expected = "LU-001 was NOT sent to Warehouse 1: the warehouse system does not answer. It still has no destination.";
                break;
            case "moving":
                // Since item 54 a step in flight makes the change wait (202); the 409 left is the mission
                // that moved while the change was being made.
                _host.Junctions = [Junction(A, C)];
                _host.MissionsMovedDuringTheChange.Add(M1);
                expected = "LU-001 was NOT sent to Lift: it moved while the change was being made - try again when its step ends. It still has no destination.";
                break;
            case "a copy moving":
                // Owner's answer 4 of 9/10: not this mission, another one sharing its cell. Only a mission
                // with a door in a warehouse has copies, and sending it elsewhere leaves the cell, so the
                // engine stops them: the tail is the warehouse it still goes to.
                _host.Missions[0] = _host.Missions[0] with { DestinationItemId = S, StorageDoorCode = "A6" };
                _host.Junctions = [Junction(A, C)];
                _host.MissionsWithACopyInFlight.Add(M1);
                expected = "LU-001 was NOT sent to Lift: another load unit sharing its cell is moving: try again in a moment. It is still bound for Warehouse 1.";
                break;
            case "cell search":
                _host.Junctions = [Junction(A, C)];
                _host.MissionsWithACellSearchInFlight.Add(M1);
                expected = "LU-001 was NOT sent to Lift: a search for its storage cell, or another change of its destination, is under way - try again when it ends. It still has no destination.";
                break;
            case "already there":
                target = A;
                expected = "LU-001 was NOT sent to Shuttle 1: it is already there. It still has no destination.";
                break;
            case "engine not loaded":
                // Row 34 of the queue: told by the host's field, not by the title.
                _host.Junctions = [Junction(A, C)];
                _host.FailRoute = "mission-destination";
                _host.FailureStatus = HttpStatusCode.ServiceUnavailable;
                _host.FailureBody = EngineNotLoadedAnswer;
                expected = "LU-001 was NOT sent to Lift: the engine is not ready (it is not loaded on the host). It still has no destination.";
                break;
            default:
                expected = "LU-001 was NOT sent to Lift: the host does not know Lift as a device. It still has no destination.";
                break;
        }

        var destinationBefore = _host.Missions[0].DestinationItemId == S ? "Warehouse 1" : "";
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, target);
        if (refusal == "unknown destination") _host.Items.RemoveAll(item => item.ItemId == C);
        AskToSetDestination(cut);
        ConfirmDestination(cut);

        cut.WaitForAssertion(() => Assert.Equal(expected, MoveOutcome(cut)));
        // What the host answered is behind the line's "?" (row 37 of the queue, extended to the whole
        // page on 10/10), and the sentence is the page's own.
        Assert.StartsWith("the host answered ", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-mission-move-outcome"));
        Assert.Equal(M1, Assert.Single(_host.MissionDestinationRequests).MissionId);
        Assert.Contains("drawio-device-list-not-written", cut.Find("#drawio-mission-move-outcome").ClassList);
        Assert.Empty(cut.FindAll("#drawio-mission-destination-clear-gateway"));
        Assert.Equal(destinationBefore, Destination(cut, M1));
        Assert.Equal(1, MissionReads);
    }

    /// <summary>
    /// The owner's decision on the gateway: a gateway that blocks the way while there is one without it
    /// is said so, and a button sends the same change again with the gateway cleared - which then goes
    /// through.
    /// </summary>
    [Fact]
    public void ARouteTheGatewayBlocksOffersToClearTheGatewayAndThatSetsTheDestination()
    {
        _host.Missions = [TestMissions.Mission(M1, A, "LU-001") with { GatewayItemId = B }, TestMissions.Mission(M2, B, "LU-002")];
        _host.Junctions = [Junction(A, C)];
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, C);
        AskToSetDestination(cut);
        ConfirmDestination(cut);

        cut.WaitForAssertion(() => Assert.Equal(
            "LU-001 was NOT sent to Lift: there is no route from Shuttle 1 to Lift passing through Shuttle 2; without the gateway there is one. It still has no destination.",
            MoveOutcome(cut)));
        var clear = cut.Find("#drawio-mission-destination-clear-gateway");
        Assert.Equal("Clear the gateway and set destination", clear.TextContent);
        Assert.Equal("", Destination(cut, M1));
        Assert.Equal("Shuttle 2", Gateway(cut, M1));

        Act(cut, () => cut.Find("#drawio-mission-destination-clear-gateway").Click());

        cut.WaitForAssertion(() => Assert.Equal("LU-001 is now bound for Lift, with no gateway: the host took it.", MoveOutcome(cut)));
        Assert.Equal([(M1, C, MissionGatewayUpdateMode.Keep, (Guid?)null), (M1, C, MissionGatewayUpdateMode.Clear, (Guid?)null)],
            _host.MissionDestinationRequests.Select(Asked));
        cut.WaitForAssertion(() => Assert.Equal("Lift", Destination(cut, M1)));
        Assert.Equal("", Gateway(cut, M1));
        Assert.Empty(cut.FindAll("#drawio-mission-destination-clear-gateway"));
    }

    /// <summary>
    /// Fix round 1: the offer to clear the gateway is the refused change, frozen with its target, and
    /// it lives only while that target is the one picked. The same target picked again keeps it;
    /// another one - from the list or from the drawing - ends it, so one click can no longer send the
    /// load unit to the destination the operator has just replaced. The refusal stays on the outcome
    /// line: it says what happened.
    /// </summary>
    [Theory]
    [InlineData("list")]
    [InlineData("drawing")]
    public async Task AnotherTargetEndsTheOfferToClearTheGateway(string pickedFrom)
    {
        _host.Items.Add(Item(D, "Conveyor", 900));
        _host.Missions = [TestMissions.Mission(M1, A, "LU-001") with { GatewayItemId = B }, TestMissions.Mission(M2, B, "LU-002")];
        _host.Junctions = [Junction(A, C)];
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, C);
        AskToSetDestination(cut);
        ConfirmDestination(cut);
        const string refused = "LU-001 was NOT sent to Lift: there is no route from Shuttle 1 to Lift passing through Shuttle 2; without the gateway there is one. It still has no destination.";
        cut.WaitForAssertion(() => Assert.Equal(refused, MoveOutcome(cut)));
        Assert.Single(cut.FindAll("#drawio-mission-destination-clear-gateway"));

        await PickAsync(C, "Lift");

        Assert.Equal("Target: Lift", cut.Find("#drawio-mission-move-target-chosen").TextContent.Trim());
        Assert.Single(cut.FindAll("#drawio-mission-destination-clear-gateway"));

        await PickAsync(D, "Conveyor");

        Assert.Equal("Target: Conveyor", cut.Find("#drawio-mission-move-target-chosen").TextContent.Trim());
        Assert.Empty(cut.FindAll("#drawio-mission-destination-clear-gateway"));
        Assert.Equal(refused, MoveOutcome(cut));
        Assert.Equal([(M1, C, MissionGatewayUpdateMode.Keep, (Guid?)null)], _host.MissionDestinationRequests.Select(Asked));
        Assert.Equal("", Destination(cut, M1));
        Assert.Equal("Shuttle 2", Gateway(cut, M1));

        async Task PickAsync(Guid target, string name)
        {
            if (pickedFrom == "list")
            {
                PickTarget(cut, target);
                return;
            }

            await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([target.ToString()]));
            cut.WaitForAssertion(() => Assert.Equal($"Selected on the drawing: {name}", cut.Find("#drawio-mission-move-selected").TextContent.Trim()));
            Act(cut, () => cut.Find("#drawio-mission-move-use-selected").Click());
        }
    }

    /// <summary>
    /// Fix round 2: a row can be chosen while the answer is on its way - towards a warehouse the engine
    /// asks the warehouse system first, and finds the gateway blocking the door it proposed only after.
    /// A refusal that lands under another row offers nothing: the button would send, with the gateway
    /// cleared, the change of a mission the page no longer shows as chosen. The same row clicked again
    /// meanwhile is still the one chosen, and the offer comes.
    /// </summary>
    [Theory]
    [InlineData("another row")]
    [InlineData("the same row")]
    public async Task ARefusalThatLandsUnderAnotherRowOffersNotToClearTheGateway(string clicked)
    {
        _host.Missions = [TestMissions.Mission(M1, A, "LU-001") with { GatewayItemId = B }, TestMissions.Mission(M2, B, "LU-002")];
        _host.Junctions = [Junction(A, C)];
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, C);
        _host.MissionDestinationArrivalGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AskToSetDestination(cut);
        ConfirmDestination(cut);
        await _host.MissionDestinationArrivalGateReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Choose(cut, clicked == "another row" ? M2 : M1);

        // The answer is still held: the row was chosen BEFORE it landed, not after.
        Assert.Equal("Asking the host to send LU-001 to Lift...", cut.Find("#drawio-mission-move-sending").TextContent);
        Assert.Empty(cut.FindAll("#drawio-mission-move-outcome"));

        _host.MissionDestinationArrivalGate.SetResult();

        cut.WaitForAssertion(() => Assert.Equal(
            "LU-001 was NOT sent to Lift: there is no route from Shuttle 1 to Lift passing through Shuttle 2; without the gateway there is one. It still has no destination.",
            MoveOutcome(cut)));
        Assert.Empty(cut.FindAll("#drawio-mission-move-sending"));
        Assert.Equal(
            clicked == "another row"
                ? "Chosen: the mission of load unit LU-002, now on Shuttle 2, with no destination."
                : "Chosen: the mission of load unit LU-001, now on Shuttle 1, with no destination.",
            cut.Find("#drawio-mission-move-chosen").TextContent.Trim());
        Assert.Equal("Target: Lift", cut.Find("#drawio-mission-move-target-chosen").TextContent.Trim());
        Assert.Equal(clicked == "another row" ? 0 : 1, cut.FindAll("#drawio-mission-destination-clear-gateway").Count);
        Assert.Equal([(M1, C, MissionGatewayUpdateMode.Keep, (Guid?)null)], _host.MissionDestinationRequests.Select(Asked));
        Assert.Equal("", Destination(cut, M1));
        Assert.Equal("Shuttle 2", Gateway(cut, M1));
    }

    /// <summary>A gateway the host does not know (400): said with the gateway's name, the row as it was.</summary>
    [Fact]
    public void AGatewayTheHostDoesNotKnowIsRefusedWithItsName()
    {
        _host.Junctions = [Junction(A, B), Junction(B, C), Junction(A, C)];
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, C);
        AskToSetDestination(cut);
        ChooseGateway(cut, "add");
        PickGateway(cut, B);
        _host.Items.RemoveAll(item => item.ItemId == B);

        ConfirmDestination(cut);

        cut.WaitForAssertion(() => Assert.Equal(
            "LU-001 was NOT sent to Lift: the host does not know Shuttle 2 as a device to pass through. It still has no destination.",
            MoveOutcome(cut)));
        Assert.Equal((M1, C, MissionGatewayUpdateMode.Set, B), Asked(Assert.Single(_host.MissionDestinationRequests)));
        Assert.Equal(1, MissionReads);
    }

    /// <summary>A mission that ended meanwhile: nothing changed, and the list is read again - its row goes.</summary>
    [Fact]
    public void ADestinationForAMissionThatEndedMeanwhileIsSaidSoAndItsRowGoes()
    {
        _host.Junctions = [Junction(A, C)];
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, C);
        AskToSetDestination(cut);
        _host.Missions.RemoveAll(m => m.MissionId == M1);

        ConfirmDestination(cut);

        cut.WaitForAssertion(() => Assert.Equal(
            "LU-001 was NOT sent to Lift: it is no longer active on the host (it ended or was removed).", MoveOutcome(cut)));
        Assert.StartsWith("the host answered 404: {", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-mission-move-outcome"));
        cut.WaitForAssertion(() => Assert.Equal([M2.ToString()], Rows(cut)));
    }

    /// <summary>
    /// A refusal of the destination this page does not know by its title: the line says the host
    /// refused it, and the host's own words are behind its "?" with the rest of the answer - not quoted
    /// in the sentence (row 37 of the queue, extended to the whole page on 10/10).
    /// </summary>
    [Fact]
    public void ARefusalOfTheDestinationThePageDoesNotKnowKeepsTheHostsWordsBehindTheQuestionMark()
    {
        _host.Junctions = [Junction(A, C)];
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, C);
        _host.FailRoute = "mission-destination";
        _host.FailureStatus = HttpStatusCode.Conflict;
        _host.FailureBody = """{"title":"Un rifiuto nuovo","detail":"Il motore non vuole (prova)"}""";

        AskToSetDestination(cut);
        ConfirmDestination(cut);

        cut.WaitForAssertion(() => Assert.Equal("LU-001 was NOT sent to Lift: the host refused it. It still has no destination.", MoveOutcome(cut)));
        Assert.Equal("the host answered 409: {\"title\":\"Un rifiuto nuovo\",\"detail\":\"Il motore non vuole (prova)\"}",
            DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-mission-move-outcome"));
        Assert.Equal(1, MissionReads);
    }

    /// <summary>
    /// An answer that never came, a 500 or an unforeseen failure: whether the destination changed is NOT
    /// known - the engine finishes a change nobody waits for - so it is said, and the list is read again.
    /// Towards a warehouse the line warns that the same request again would ask for another cell, and
    /// every line ends with the row as the host has it now.
    /// </summary>
    [Theory]
    [InlineData("timeout", false, "the host did not answer in time", "TaskCanceledException: La richiesta su 'mission-destination' e' scaduta senza risposta (prova).")]
    [InlineData("transport", false, "the connection to the host failed", "HttpRequestException: Il trasporto su 'mission-destination' e' fallito (Unknown) (prova).")]
    [InlineData("server error", false, "the host answered with an error", "the host answered 500 with an empty body")]
    [InlineData("unforeseen", false, "an unforeseen failure stopped it", "InvalidOperationException: something nobody expected (test)")]
    [InlineData("timeout", true, "the host did not answer in time", "TaskCanceledException: La richiesta su 'mission-destination' e' scaduta senza risposta (prova).")]
    public void ADestinationWhoseOutcomeIsNotKnownIsSaidSoAndTheListIsReadAgain(string failure, bool towardsAWarehouse, string why, string answer)
    {
        _host.Items.Add(Item(S, "Warehouse 1", 900, DeviceTypes.Storage));
        _host.Junctions = [Junction(A, C), Junction(A, S)];
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, towardsAWarehouse ? S : C);
        switch (failure)
        {
            case "timeout": _host.TimeoutRoute = "mission-destination"; break;
            case "transport": _host.HttpFailureRoute = "mission-destination"; break;
            case "server error":
                _host.FailRoute = "mission-destination";
                _host.FailureStatus = HttpStatusCode.InternalServerError;
                break;
            default: _host.MissionDestinationFailure = new InvalidOperationException("something nobody expected (test)"); break;
        }

        AskToSetDestination(cut);
        ConfirmDestination(cut);

        var target = towardsAWarehouse ? "Warehouse 1" : "Lift";
        // One sentence with the why in the page's words, what came back behind its "?" (row 37 of the
        // queue, extended to the whole page on 10/10).
        cut.WaitForAssertion(() => Assert.StartsWith($"It is NOT known whether LU-001 was sent to {target}, because {why}. The list is read again from the host.", MoveOutcome(cut)));
        Assert.Equal(answer, DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-mission-move-outcome"));
        cut.WaitForAssertion(() => Assert.EndsWith(" Read again: with no destination, with no gateway.", MoveOutcome(cut)));
        if (towardsAWarehouse)
            Assert.Contains("The list is read again from the host. A new request waits for that read, which shows the gateway as it is now:"
                + " if this change went through, the same request again asks the warehouse system for another cell in Warehouse 1."
                + " Read again: with no destination, with no gateway.", MoveOutcome(cut));
        else
            Assert.DoesNotContain("another cell", MoveOutcome(cut));
        Assert.Equal(2, MissionReads);
    }

    /// <summary>
    /// d6 §8, the retry trap: a change of the gateway alone whose answer was lost may have gone through,
    /// and then the same request again finds the gateway already written - it is no longer a change of
    /// the gateway, and towards the same warehouse it asks for another cell. So the page reads the row
    /// again BEFORE it lets the operator ask again - also when a read was already on its way when the
    /// answer was lost, which may predate the change - and the new question shows the gateway as the
    /// host has it now.
    /// </summary>
    [Fact]
    public async Task AfterAnUnknownOutcomeTheRowIsReadAgainBeforeAskingAgain()
    {
        _host.Items.Add(Item(S, "Warehouse 1", 900, DeviceTypes.Storage));
        _host.Missions[0] = _host.Missions[0] with { DestinationItemId = S, StorageDoorCode = "S01A" };
        _host.Junctions = [Junction(A, C), Junction(C, S)];
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, S);
        AskToSetDestination(cut);
        ChooseGateway(cut, "add");
        PickGateway(cut, C);
        _host.TimeoutRoute = "mission-destination";
        // A read of the list is already on its way when the answer is lost: the host froze its rows
        // when it took the request, so that read answers the row from BEFORE the change, with no
        // gateway. Only a read that began after the outcome can see the gateway.
        _host.MissionsGate = new TaskCompletionSource();
        // The click's own task: it ends after the render that ComponentBase makes when an async handler
        // finishes, which comes AFTER the render showing "Read again" (see below).
        var reread = Task.CompletedTask;
        Act(cut, () => reread = cut.Find("#drawio-mission-list-reread").ClickAsync(new MouseEventArgs()));
        await _host.MissionsGateReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // The change goes through on the host, and its answer is lost on the way back. The engine's
        // commit raises the mark of the operator's choice with it.
        _host.Missions[0] = _host.Missions[0] with { GatewayItemId = C, OperatorDestination = true };

        ConfirmDestination(cut);

        cut.WaitForAssertion(() => Assert.StartsWith("It is NOT known whether LU-001 was sent to Warehouse 1, because ", MoveOutcome(cut)));
        Assert.DoesNotContain("Read again:", MoveOutcome(cut));
        Assert.True(cut.Find("#drawio-mission-destination-button").HasAttribute("disabled"));
        _host.TimeoutRoute = null;
        _host.MissionsGate.SetResult();

        cut.WaitForAssertion(() => Assert.EndsWith(" Read again: bound for Warehouse 1, passing through Lift.", MoveOutcome(cut)));
        // The read ran inside the click on "Read again", and that click's handler ends with one more
        // render, which gives every button a new event handler ID. Clicking the button found before
        // that render failed now and then with UnknownEventHandlerIdException (ID 365, the button's ID
        // before the render; 404 after it): the test waits for the click to end, not for time.
        await reread.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(cut.Find("#drawio-mission-destination-button").HasAttribute("disabled"));
        AskToSetDestination(cut);
        Assert.Equal("Gateway: Lift", cut.Find("#drawio-mission-destination-gateway-now").TextContent);
        Assert.Equal("Send LU-001 to Warehouse 1 again? This asks the warehouse system for another cell in Warehouse 1."
            + " Changing the gateway instead keeps the cell it has and only computes its route again.",
            cut.Find("#drawio-mission-destination-question").TextContent);
    }

    /// <summary>
    /// The first read of the list failing: why in the page's words, and the host's answer behind the
    /// line's "?" (row 37 of the queue, extended to the whole page on 10/10) - where it used to quote the
    /// client's "L'API del motore ha risposto 500".
    /// </summary>
    [Fact]
    public void AFirstReadTheHostFailsIsOneSentenceWithTheAnswerBehindTheQuestionMark()
    {
        var cut = RenderPage();
        _host.BrokenReads.Add("missions");

        Act(cut, () => cut.Find("#drawio-mission-list-toggle").Click());

        cut.WaitForAssertion(() => Assert.Equal("The missions could not be read from the host because the host answered with an error.",
            cut.Find("#drawio-mission-list-read-error").TextContent));
        Assert.Equal("the host answered 500 with an empty body", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-mission-list-read-error"));
    }

    /// <summary>
    /// A read that fails after an unknown outcome shows nothing new: asking again still waits, until a
    /// read goes through ("Read again").
    /// </summary>
    [Fact]
    public void AfterAnUnknownOutcomeAFailedReadStillHoldsTheNextRequest()
    {
        _host.Junctions = [Junction(A, C)];
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, C);
        _host.TimeoutRoute = "mission-destination";
        _host.BrokenReads.Add("missions");
        AskToSetDestination(cut);
        ConfirmDestination(cut);

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("#drawio-mission-list-read-error")));
        // The read that failed says why in the page's words, the host's answer behind its "?" (row 37
        // of the queue, extended to the whole page on 10/10).
        Assert.Equal("The missions could not be read again from the host because the host answered with an error: the rows below are how the host had them at the last read, with what the channel said since.",
            cut.Find("#drawio-mission-list-read-error").TextContent);
        Assert.Equal("the host answered 500 with an empty body", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-mission-list-read-error"));
        Assert.True(cut.Find("#drawio-mission-destination-button").HasAttribute("disabled"));
        Assert.DoesNotContain("Read again:", MoveOutcome(cut));

        _host.BrokenReads.Remove("missions");
        _host.TimeoutRoute = null;
        Act(cut, () => cut.Find("#drawio-mission-list-reread").Click());

        cut.WaitForAssertion(() => Assert.EndsWith(" Read again: with no destination, with no gateway.", MoveOutcome(cut)));
        Assert.False(cut.Find("#drawio-mission-destination-button").HasAttribute("disabled"));
    }

    /// <summary>
    /// The same warehouse chosen again with the gateway as it is is a request for a cell there again
    /// (owner's decision of 1/10): another one when the operator had chosen it, possibly the same one
    /// when the engine had. The row carries the mark of the operator's choice
    /// (<see cref="MissionDto.OperatorDestination"/>), and the question says only the true case (row 44
    /// of the queue, owner's decision of 10/10) - it used to say both. It also says that changing the
    /// gateway instead keeps the cell (decision 6 of 2/10).
    /// </summary>
    [Theory]
    [InlineData(true, "Send LU-001 to Warehouse 1 again? This asks the warehouse system for another cell in Warehouse 1.")]
    [InlineData(false, "Send LU-001 to Warehouse 1 again? This asks the warehouse system for a cell in Warehouse 1 again.")]
    public void TheSameWarehouseAgainSaysOnlyTheTrueCase(bool operatorChose, string question)
    {
        _host.Items.Add(Item(S, "Warehouse 1", 900, DeviceTypes.Storage));
        _host.Missions[0] = _host.Missions[0] with { DestinationItemId = S, StorageDoorCode = "S01A", OperatorDestination = operatorChose };
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, S);

        AskToSetDestination(cut);

        Assert.Equal(question + " Changing the gateway instead keeps the cell it has and only computes its route again.",
            cut.Find("#drawio-mission-destination-question").TextContent);
    }

    /// <summary>
    /// The fake host carries the mark as the real one does: a change that goes through raises it (the
    /// engine's commit, PathMission.SetOperatorDestination), so the same warehouse asked again from the
    /// row read after it is "another cell"; and a move onto the destination drops it (the setter of
    /// PathMission.PositionItemId), as it drops a gateway reached.
    /// </summary>
    [Fact]
    public void TheMarkIsRaisedByAChangeAndDroppedOnArrival()
    {
        _host.Items.Add(Item(S, "Warehouse 1", 900, DeviceTypes.Storage));
        _host.Missions[0] = _host.Missions[0] with { DestinationItemId = C, StorageDoorCode = "S01A" };
        _host.Junctions = [Junction(A, S)];
        var cut = RenderPage();
        OpenTheList(cut);
        Assert.Equal("No", CellText(cut, M1, nameof(MissionDto.OperatorDestination)));
        Choose(cut, M1);
        PickTarget(cut, S);
        AskToSetDestination(cut);
        ConfirmDestination(cut);
        cut.WaitForAssertion(() => Assert.Equal("Yes", CellText(cut, M1, nameof(MissionDto.OperatorDestination))));

        AskToSetDestination(cut);

        Assert.Equal("Send LU-001 to Warehouse 1 again? This asks the warehouse system for another cell in Warehouse 1."
            + " Changing the gateway instead keeps the cell it has and only computes its route again.",
            cut.Find("#drawio-mission-destination-question").TextContent);

        Act(cut, () => cut.Find("#drawio-mission-destination-cancel").Click());
        AskAndConfirm(cut);

        cut.WaitForAssertion(() => Assert.Equal("Warehouse 1", Position(cut, M1)));
        Assert.Equal("No", CellText(cut, M1, nameof(MissionDto.OperatorDestination)));
    }

    /// <summary>
    /// Decision 6 of 2/10: towards the same warehouse, a gateway cleared, set where there was none, or
    /// changed for another keeps the cell and only computes the route again - the question follows the
    /// radios, and the engine (here its fake) asks the warehouse system nothing: a warehouse without a
    /// free cell does not refuse it. Picking the gateway it already has is no change.
    /// </summary>
    [Theory]
    [InlineData("clear")]
    [InlineData("change")]
    [InlineData("add")]
    public void ChangingTheGatewayOfTheSameWarehouseKeepsTheCell(string option)
    {
        _host.Items.Add(Item(S, "Warehouse 1", 900, DeviceTypes.Storage));
        var gatewayNow = option == "add" ? (Guid?)null : B;
        _host.Missions[0] = _host.Missions[0] with { DestinationItemId = S, StorageDoorCode = "S01A", GatewayItemId = gatewayNow };
        // The engine keeps the cell when the mission's door is in the warehouse chosen, read from the door
        // map (MissionGateway.DoorInTheChosenStoragesAsync), not from its destination (row 51).
        _host.DoorMappings.Add(new DoorMappingDto(1, S, "S01A", 1m, null, 0, false));
        _host.Junctions = [Junction(A, S), Junction(A, C), Junction(C, S), Junction(A, B), Junction(B, S)];
        _host.StoragesWithoutAFreeCell.Add(S);
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, S);
        AskToSetDestination(cut);

        ChooseGateway(cut, option);
        if (option == "change")
        {
            PickGateway(cut, B);
            Assert.StartsWith("Send LU-001 to Warehouse 1 again? This asks the warehouse system for a cell in Warehouse 1 again",
                cut.Find("#drawio-mission-destination-question").TextContent);
        }

        if (option != "clear") PickGateway(cut, C);
        var through = option == "clear" ? ", with no gateway" : ", passing through Lift";
        Assert.Equal($"Send LU-001 to Warehouse 1 again{through}? It keeps the cell it has in Warehouse 1: only its route is computed again.",
            cut.Find("#drawio-mission-destination-question").TextContent);

        ConfirmDestination(cut);

        cut.WaitForAssertion(() => Assert.Equal($"LU-001 is now bound for Warehouse 1{through}: the host took it.", MoveOutcome(cut)));
        Assert.Equal("S01A", CellText(cut, M1, nameof(MissionDto.StorageDoorCode)));
    }

    /// <summary>
    /// A gateway picked that the load unit has already reached - its position, or the position's group -
    /// is no gateway for the engine (MissionGateway.ChangeDestinationAsync drops it, PathMission.Reaches):
    /// with no gateway on the row nothing changes, and the same warehouse again asks the warehouse
    /// system for a cell again, so the question must not promise the cell; with a gateway on the row it
    /// clears that one, which keeps the cell.
    /// </summary>
    [Theory]
    [InlineData("the position, with no gateway")]
    [InlineData("the position's group, with no gateway")]
    [InlineData("the position, with a gateway")]
    public void AGatewayAlreadyReachedIsNoGateway(string picked)
    {
        _host.Items.Add(Item(S, "Warehouse 1", 900, DeviceTypes.Storage));
        _host.Items.Add(Item(D, "Lifts", 1200, DeviceTypes.GroupOfAutomationDevices) with { GroupItemId = 7 });
        _host.Items[_host.Items.FindIndex(item => item.ItemId == C)] = Item(C, "Lift", 600) with { BelongingGroupId = 7 };
        var gatewayNow = picked.EndsWith("with a gateway") ? (Guid?)B : null;
        _host.Missions[0] = _host.Missions[0] with { PositionItemId = C, DestinationItemId = S, StorageDoorCode = "S01A", GatewayItemId = gatewayNow };
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, S);
        AskToSetDestination(cut);

        ChooseGateway(cut, gatewayNow is null ? "add" : "change");
        PickGateway(cut, picked.StartsWith("the position's group") ? D : C);

        Assert.Equal(gatewayNow is null
                ? "Send LU-001 to Warehouse 1 again? This asks the warehouse system for a cell in Warehouse 1 again."
                  + " Changing the gateway instead keeps the cell it has and only computes its route again."
                : "Send LU-001 to Warehouse 1 again, with no gateway? It keeps the cell it has in Warehouse 1: only its route is computed again.",
            cut.Find("#drawio-mission-destination-question").TextContent);
    }

    /// <summary>
    /// A mission bound for a warehouse but without a cell there (it lost it, or its cell is searched
    /// later) has no cell to keep: the question promises none, and a gateway changed with it runs the
    /// whole request - a warehouse without a free cell then says it has none to give, not "no other".
    /// </summary>
    [Fact]
    public void WithoutACellTheSameWarehouseAgainPromisesNoCell()
    {
        _host.Items.Add(Item(S, "Warehouse 1", 900, DeviceTypes.Storage));
        _host.Missions[0] = _host.Missions[0] with { DestinationItemId = S, GatewayItemId = B };
        _host.Junctions = [Junction(A, S), Junction(A, B), Junction(B, S)];
        _host.StoragesWithoutAFreeCell.Add(S);
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, S);
        AskToSetDestination(cut);

        Assert.Equal("Send LU-001 to Warehouse 1 again? It has no cell there now: this asks the warehouse system for one in Warehouse 1.",
            cut.Find("#drawio-mission-destination-question").TextContent);
        ChooseGateway(cut, "clear");
        Assert.Equal("Send LU-001 to Warehouse 1 again? It has no cell there now: this asks the warehouse system for one in Warehouse 1.",
            cut.Find("#drawio-mission-destination-question").TextContent);

        ConfirmDestination(cut);

        cut.WaitForAssertion(() => Assert.Equal(
            "LU-001 was NOT sent to Warehouse 1: the warehouse system has no cell to give it in Warehouse 1 (the engine's log says why). It is still bound for Warehouse 1.",
            MoveOutcome(cut)));
    }

    /// <summary>
    /// A group whose members include a warehouse is a warehouse for the engine (it reserves a cell in
    /// one of them), and the destination the engine writes is the member it reserved in: the group
    /// chosen again over a row bound for its member is the same warehouse again, not "instead of" it.
    /// </summary>
    [Fact]
    public void AGroupOfWarehousesIsAskedAsAWarehouse()
    {
        _host.Items.Add(Item(D, "Warehouses", 900, DeviceTypes.GroupOfAutomationDevices) with { GroupItemId = 7 });
        _host.Items.Add(Item(S, "Warehouse 1", 1200, DeviceTypes.Storage) with { BelongingGroupId = 7 });
        _host.Missions[0] = _host.Missions[0] with { DestinationItemId = S, StorageDoorCode = "S01A" };
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, D);

        AskToSetDestination(cut);

        Assert.Equal("Send LU-001 to Warehouses again? This asks the warehouse system for a cell in Warehouses again."
            + " Changing the gateway instead keeps the cell it has and only computes its route again.",
            cut.Find("#drawio-mission-destination-question").TextContent);
    }

    /// <summary>
    /// The group chosen again over a row bound for its member, with the gateway changed: the engine keeps
    /// the cell the mission has, because its door is in one of the group's warehouses
    /// (MissionGateway.DoorInTheChosenStoragesAsync, StoragesOf), asks the warehouse system nothing - a
    /// group without a free cell does not refuse it - and the destination stays the member, where the
    /// door is (KeepTheCellAndRouteAgain). The page promises the cell, and after the change the row is
    /// still bound for the member with the same door. Until row 51 the fake compared the destination with
    /// the group, did not keep the cell, and wrote the group as the destination: an answer the engine does
    /// not give.
    /// </summary>
    [Fact]
    public void TheGroupChosenAgainOverItsMemberWithTheGatewayChangedKeepsTheCell()
    {
        _host.Items.Add(Item(D, "Warehouses", 900, DeviceTypes.GroupOfAutomationDevices) with { GroupItemId = 7 });
        _host.Items.Add(Item(S, "Warehouse 1", 1200, DeviceTypes.Storage) with { BelongingGroupId = 7 });
        _host.Missions[0] = _host.Missions[0] with { DestinationItemId = S, StorageDoorCode = "S01A", GatewayItemId = B };
        _host.DoorMappings.Add(new DoorMappingDto(1, S, "S01A", 1m, null, 0, false));
        _host.Junctions = [Junction(A, S), Junction(A, D), Junction(A, B), Junction(B, S), Junction(B, D)];
        _host.StoragesWithoutAFreeCell.Add(D);
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, D);
        AskToSetDestination(cut);
        ChooseGateway(cut, "clear");

        Assert.Equal("Send LU-001 to Warehouses again, with no gateway? It keeps the cell it has in Warehouses: only its route is computed again.",
            cut.Find("#drawio-mission-destination-question").TextContent);
        ConfirmDestination(cut);

        cut.WaitForAssertion(() => Assert.Equal("Warehouse 1", Destination(cut, M1)));
        Assert.Equal("S01A", CellText(cut, M1, nameof(MissionDto.StorageDoorCode)));
        Assert.Null(_host.Missions[0].GatewayItemId);
    }

    /// <summary>
    /// A storage outside the group chosen is not the group again: it is a change "instead of" it.
    /// </summary>
    [Fact]
    public void AWarehouseOutsideTheGroupIsNotTheGroupAgain()
    {
        _host.Items.Add(Item(D, "Warehouses", 900, DeviceTypes.GroupOfAutomationDevices) with { GroupItemId = 7 });
        _host.Items.Add(Item(S, "Warehouse 1", 1200, DeviceTypes.Storage) with { BelongingGroupId = 7 });
        _host.Items.Add(Item(W2, "Warehouse 2", 1500, DeviceTypes.Storage) with { BelongingGroupId = 8 });
        _host.Missions[0] = _host.Missions[0] with { DestinationItemId = W2, StorageDoorCode = "S01A" };
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, D);

        AskToSetDestination(cut);

        Assert.Equal("Send LU-001 to Warehouses instead of Warehouse 2?", cut.Find("#drawio-mission-destination-question").TextContent);
    }

    /// <summary>
    /// Answers 1 and 3 of 2/10 on an extraction sent to a warehouse: picked up already, the warehouse
    /// system is told to reopen its order, and an order already shipped stays as it is (item 45); still in its
    /// cell (on the warehouse, or nowhere with the warehouse as its source), it becomes a change of cell
    /// and its order - an assignment, or a row of the extraction queue - goes back free; only a refusal
    /// after the warehouse system was told ends the mission, and without an order nothing is freed and
    /// nothing ends it (MissionGateway.LeaveTheExtractionAsync). Only a warehouse its position reaches -
    /// the warehouse it is on, or that warehouse's group - is "already there" for the engine
    /// (PathMission.Reaches): nowhere reaches nothing, so its own warehouse, its source, is a change of
    /// cell like another. Any other change says nothing of an order.
    /// </summary>
    [Theory]
    [InlineData("picked up")]
    [InlineData("in its cell, on the warehouse")]
    [InlineData("in its cell, nowhere")]
    [InlineData("in its cell, with no order")]
    [InlineData("in its cell, towards a device")]
    [InlineData("in its cell, towards its own warehouse")]
    [InlineData("in its cell, towards its own warehouse's group")]
    [InlineData("in its cell, nowhere, towards its own warehouse")]
    [InlineData("not an extraction")]
    public void AnExtractionSentToAWarehouseSaysWhatHappensToItsOrder(string extraction)
    {
        _host.Items.Add(Item(S, "Warehouse 1", 900, DeviceTypes.Storage));
        _host.Items.Add(Item(W2, "Warehouse 2", 1200, DeviceTypes.Storage));
        var mission = _host.Missions[0] with { MissionType = MissionTypes.Extraction, DestinationItemId = C };
        var target = S;
        string expected;
        switch (extraction)
        {
            case "picked up":
                expected = "Send LU-001 to Warehouse 1 instead of Lift? It was picked up for an order: the warehouse system is told to"
                    + " reopen that order, and an order already shipped stays as it is.";
                break;
            case "in its cell, on the warehouse":
            case "in its cell, nowhere":
                // On the warehouse with an assignment, nowhere with a row of the extraction queue: either is an order.
                mission = extraction.EndsWith("warehouse")
                    ? mission with { PositionItemId = W2, AssignmentGuid = Guid.Parse("0a55e9d0-0000-0000-0000-000000000001") }
                    : mission with { PositionItemId = null, SourceItemId = W2, ExtractionQueueId = 31 };
                expected = "Send LU-001 to Warehouse 1 instead of Lift? It is still in its cell in Warehouse 2: this makes it a change of cell,"
                    + " and its order goes back free in the warehouse system."
                    + " If the change is refused after the warehouse system was told, the mission ends and the load unit stays in its cell.";
                break;
            case "in its cell, with no order":
                mission = mission with { PositionItemId = W2, AssignmentGuid = Guid.Empty, ExtractionQueueId = 0 };
                expected = "Send LU-001 to Warehouse 1 instead of Lift? It is still in its cell in Warehouse 2: this makes it a change of cell."
                    + " It has no order for the warehouse system to free.";
                break;
            case "in its cell, towards a device":
                mission = mission with { PositionItemId = W2, DestinationItemId = S };
                target = C;
                expected = "Send LU-001 to Lift instead of Warehouse 1?";
                break;
            case "in its cell, towards its own warehouse":
                mission = mission with { PositionItemId = S, AssignmentGuid = Guid.Parse("0a55e9d0-0000-0000-0000-000000000001") };
                expected = "Send LU-001 to Warehouse 1 instead of Lift?";
                break;
            case "in its cell, towards its own warehouse's group":
                _host.Items.Add(Item(D, "Warehouses", 1500, DeviceTypes.GroupOfAutomationDevices) with { GroupItemId = 7 });
                _host.Items[_host.Items.FindIndex(item => item.ItemId == S)] = Item(S, "Warehouse 1", 900, DeviceTypes.Storage) with { BelongingGroupId = 7 };
                mission = mission with { PositionItemId = S, AssignmentGuid = Guid.Parse("0a55e9d0-0000-0000-0000-000000000001") };
                target = D;
                expected = "Send LU-001 to Warehouses instead of Lift?";
                break;
            case "in its cell, nowhere, towards its own warehouse":
                mission = mission with { PositionItemId = null, SourceItemId = S, AssignmentGuid = Guid.Parse("0a55e9d0-0000-0000-0000-000000000001") };
                expected = "Send LU-001 to Warehouse 1 instead of Lift? It is still in its cell in Warehouse 1: this makes it a change of cell,"
                    + " and its order goes back free in the warehouse system."
                    + " If the change is refused after the warehouse system was told, the mission ends and the load unit stays in its cell.";
                break;
            default:
                mission = mission with { MissionType = MissionTypes.MovingToItem };
                expected = "Send LU-001 to Warehouse 1 instead of Lift?";
                break;
        }

        _host.Missions[0] = mission;
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, target);

        AskToSetDestination(cut);

        Assert.Equal(expected, cut.Find("#drawio-mission-destination-question").TextContent);
    }

    /// <summary>
    /// Answer 2 of 2/10 and task 4: a change of cell refused AFTER the warehouse system was told that
    /// the load unit leaves its order closes the mission, and the host's refusal says so (missionClosed):
    /// the page says it, and reads the list again, which drops the row. A refusal that left the mission
    /// as it was says only the refusal.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AChangeOfCellRefusedAfterTheWarehouseWasToldSaysTheMissionEnded(bool closed)
    {
        _host.Items.Add(Item(S, "Warehouse 1", 900, DeviceTypes.Storage));
        _host.Items.Add(Item(W2, "Warehouse 2", 1200, DeviceTypes.Storage));
        _host.Missions[0] = _host.Missions[0] with
        {
            MissionType = MissionTypes.Extraction, PositionItemId = W2, DestinationItemId = C,
            AssignmentGuid = Guid.Parse("0a55e9d0-0000-0000-0000-000000000001"),
        };
        _host.Junctions = [Junction(W2, S), Junction(W2, C)];
        if (closed)
        {
            _host.StoragesWithoutAFreeCell.Add(S);
            _host.ChangeOfCellRefusedAfterTheReports = true;
        }
        else
        {
            _host.WarehouseUnreachable = true;
        }

        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, S);
        AskToSetDestination(cut);
        ConfirmDestination(cut);

        if (closed)
        {
            cut.WaitForAssertion(() => Assert.Equal(
                "LU-001 was NOT sent to Warehouse 1: the warehouse system has no cell to give it in Warehouse 1 (the engine's log says why)."
                + " The mission was closed: the warehouse system had already been told that it leaves its order, so the load unit stays in its cell with no order.",
                MoveOutcome(cut)));
            cut.WaitForAssertion(() => Assert.Equal([M2.ToString()], Rows(cut)));
        }
        else
        {
            cut.WaitForAssertion(() => Assert.Equal(2, MissionReads));
            cut.WaitForAssertion(() => Assert.Equal(
                "LU-001 was NOT sent to Warehouse 1: the warehouse system does not answer. It is still bound for Lift.", MoveOutcome(cut)));
            Assert.Equal(2, Rows(cut).Count);
        }
    }

    /// <summary>
    /// Answer 4 of 8/10, evening: the warehouse system said no (a 4xx) to the first report of the change
    /// of cell. It got it and did not apply it, so the change is refused and the extraction stays as it
    /// was; the page says the warehouse system refused the change, not that it does not answer (before, the
    /// host carried the 4xx as the 503 of a warehouse system that does not answer, and the page said so).
    /// </summary>
    [Fact]
    public void AChangeOfCellTheWarehouseRefusedSaysSo()
    {
        _host.Items.Add(Item(S, "Warehouse 1", 900, DeviceTypes.Storage));
        _host.Items.Add(Item(W2, "Warehouse 2", 1200, DeviceTypes.Storage));
        _host.Missions[0] = _host.Missions[0] with
        {
            MissionType = MissionTypes.Extraction, PositionItemId = W2, DestinationItemId = C,
            AssignmentGuid = Guid.Parse("0a55e9d0-0000-0000-0000-000000000001"),
        };
        _host.Junctions = [Junction(W2, S), Junction(W2, C)];
        _host.WarehouseRefusesTheChangeOfCell = true;

        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, S);
        AskToSetDestination(cut);
        ConfirmDestination(cut);

        cut.WaitForAssertion(() => Assert.Equal(2, MissionReads));
        cut.WaitForAssertion(() => Assert.Equal(
            "LU-001 was NOT sent to Warehouse 1: the warehouse system refused the change. It is still bound for Lift.", MoveOutcome(cut)));
        Assert.Equal(2, Rows(cut).Count);
    }

    /// <summary>
    /// The closure is what the host says, not what a read shows (final review of voce 39). The host
    /// refuses a change of cell with missionClosed, and the list read again still shows the row - a
    /// read already in flight, or the channel late: the page says the mission was closed all the same,
    /// and does not say it is still bound anywhere. And a refusal without the field says only the
    /// refusal, whatever the read shows. Before, the page guessed the closure from the row being gone
    /// after the read, and with the row still there said "It is still bound for Lift."
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheClosureIsWhatTheHostSaysNotWhatTheReadShows(bool closed)
    {
        _host.Items.Add(Item(S, "Warehouse 1", 900, DeviceTypes.Storage));
        _host.Items.Add(Item(W2, "Warehouse 2", 1200, DeviceTypes.Storage));
        _host.Missions[0] = _host.Missions[0] with
        {
            MissionType = MissionTypes.Extraction, PositionItemId = W2, DestinationItemId = C,
            AssignmentGuid = Guid.Parse("0a55e9d0-0000-0000-0000-000000000001"),
        };
        _host.Junctions = [Junction(W2, S), Junction(W2, C)];
        _host.FailRoute = "mission-destination";
        _host.FailureStatus = HttpStatusCode.UnprocessableEntity;
        _host.FailureBody = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["title"] = MissionDestinationProblems.NoStorageCellTitle,
            ["status"] = 422,
            [MissionDestinationProblems.NoOtherFreeCellExtension] = false,
            [MissionDestinationProblems.MissionClosedExtension] = closed,
        });

        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, S);
        AskToSetDestination(cut);
        ConfirmDestination(cut);

        cut.WaitForAssertion(() => Assert.Equal(2, MissionReads));
        cut.WaitForAssertion(() => Assert.Equal(
            "LU-001 was NOT sent to Warehouse 1: the warehouse system has no cell to give it in Warehouse 1 (the engine's log says why)."
            + (closed
                ? " The mission was closed: the warehouse system had already been told that it leaves its order, so the load unit stays in its cell with no order."
                : " It is still bound for Lift."),
            MoveOutcome(cut)));
        Assert.Equal(2, Rows(cut).Count);
    }

    /// <summary>
    /// "Again" is the same device, not the same name: a target named like the destination the mission
    /// has, but another device, is a change "instead of" it.
    /// </summary>
    [Fact]
    public void ADeviceNamedLikeTheDestinationIsNotTheSameDestination()
    {
        _host.Items.Add(Item(D, "Lift", 900));
        _host.Missions[0] = _host.Missions[0] with { DestinationItemId = C };
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, D);

        AskToSetDestination(cut);

        Assert.Equal("Send LU-001 to Lift instead of Lift?", cut.Find("#drawio-mission-destination-question").TextContent);
    }

    /// <summary>
    /// A load unit's code is the identifier on its label: a sentence that starts with it does not
    /// capitalize it ("uc42", not "Uc42").
    /// </summary>
    [Fact]
    public void ALoadUnitCodeIsNotCapitalizedAtTheStartOfASentence()
    {
        _host.Missions[0] = _host.Missions[0] with { LoadUnitCode = "uc42" };
        _host.Junctions = [Junction(A, C)];
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, C);
        AskToSetDestination(cut);
        ConfirmDestination(cut);

        cut.WaitForAssertion(() => Assert.Equal("uc42 is now bound for Lift: the host took it.", MoveOutcome(cut)));
    }

    /// <summary>
    /// A gateway the load unit has already reached is dropped by the engine, not written: the outcome
    /// says what the host holds after the change (no gateway, and why), not the passage that was asked.
    /// </summary>
    [Fact]
    public void AGatewayAlreadyReachedIsNotPromisedInTheOutcome()
    {
        _host.Junctions = [Junction(A, C)];
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, C);
        AskToSetDestination(cut);
        ChooseGateway(cut, "add");
        Act(cut, () => cut.Find("#drawio-mission-destination-gateway-search").Input("shuttle 1"));
        PickGateway(cut, A);

        ConfirmDestination(cut);

        cut.WaitForAssertion(() => Assert.Equal(
            "LU-001 is now bound for Lift, with no gateway (it has already reached Shuttle 1): the host took it.", MoveOutcome(cut)));
        Assert.Equal((M1, C, MissionGatewayUpdateMode.Set, A), Asked(Assert.Single(_host.MissionDestinationRequests)));
        Assert.Equal("", Gateway(cut, M1));
    }

    /// <summary>
    /// A refusal that came after the reservation had let go of the cell the mission held: the
    /// destination did not change, but the mission lost its cell, and the outcome says so - and that
    /// the engine is reserving another one in the warehouse it is still bound for.
    /// </summary>
    [Fact]
    public void ARefusalThatLostTheCellSaysSo()
    {
        _host.Items.Add(Item(S, "Warehouse 1", 900, DeviceTypes.Storage));
        _host.Items.Add(Item(D, "Warehouse 2", 1200, DeviceTypes.Storage));
        _host.Missions[0] = _host.Missions[0] with { DestinationItemId = S, StorageDoorCode = "S01A" };
        _host.Junctions = [Junction(A, S), Junction(A, D)];
        _host.WarehouseUnreachableAfterReserving = true;
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, D);
        AskToSetDestination(cut);
        ConfirmDestination(cut);

        cut.WaitForAssertion(() => Assert.Equal(
            "LU-001 was NOT sent to Warehouse 2: the warehouse system does not answer. It is still bound for Warehouse 1."
            + " Its cell was given back to the warehouse system on the way, so it has no cell now: the engine is reserving another one in Warehouse 1.",
            MoveOutcome(cut)));
    }

    /// <summary>
    /// The offer to clear the gateway does not follow the row: another row chosen ends it, as another
    /// target does - its button names neither, and would send the change of a mission the page no
    /// longer shows as chosen.
    /// </summary>
    [Fact]
    public void AnotherRowEndsTheOfferToClearTheGateway()
    {
        _host.Missions = [TestMissions.Mission(M1, A, "LU-001") with { GatewayItemId = B }, TestMissions.Mission(M2, B, "LU-002")];
        _host.Junctions = [Junction(A, C)];
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, C);
        AskToSetDestination(cut);
        ConfirmDestination(cut);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("#drawio-mission-destination-clear-gateway")));

        Choose(cut, M2);

        Assert.Empty(cut.FindAll("#drawio-mission-destination-clear-gateway"));
        Choose(cut, M1);
        Assert.Empty(cut.FindAll("#drawio-mission-destination-clear-gateway"));
    }

    /// <summary>
    /// While a question is open the offer to clear the gateway is not shown: one change at a time, and
    /// the button would send the refused one under a question about another.
    /// </summary>
    [Fact]
    public void TheOfferToClearTheGatewayHidesWhileAQuestionIsOpen()
    {
        _host.Missions = [TestMissions.Mission(M1, A, "LU-001") with { GatewayItemId = B }, TestMissions.Mission(M2, B, "LU-002")];
        _host.Junctions = [Junction(A, C)];
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, C);
        AskToSetDestination(cut);
        ConfirmDestination(cut);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("#drawio-mission-destination-clear-gateway")));

        AskToSetDestination(cut);

        Assert.Single(cut.FindAll("#drawio-mission-destination-question"));
        Assert.Empty(cut.FindAll("#drawio-mission-destination-clear-gateway"));
        Act(cut, () => cut.Find("#drawio-mission-destination-cancel").Click());
        Assert.Single(cut.FindAll("#drawio-mission-destination-clear-gateway"));
    }

    // ---- Item 54: the relocator and the precise cell ------------------------------------------------

    /// <summary>A warehouse whose relocator is Lift (C): Lift is a relocator (MissionGateway.IsARelocator), the warehouse a target with cells.</summary>
    private void AWarehouseRelocatedByTheLift()
    {
        _host.Items.Add(Item(S, "Warehouse 1", 900, DeviceTypes.Storage) with { RelocateItemId = C });
        _host.Junctions = [Junction(A, C), Junction(A, S)];
    }

    private static void ChooseArrival(IRenderedComponent<DrawioEditor> cut, string option) =>
        Act(cut, () => cut.Find($"#drawio-mission-destination-relocator-{option}").Change(true));

    private static void ChooseCell(IRenderedComponent<DrawioEditor> cut, string option) =>
        Act(cut, () => cut.Find($"#drawio-mission-destination-cell-{option}").Change(true));

    private static string Question(IRenderedComponent<DrawioEditor> cut) =>
        cut.Find("#drawio-mission-destination-question").TextContent;

    private static bool ConfirmDisabled(IRenderedComponent<DrawioEditor> cut) =>
        cut.Find("#drawio-mission-destination-confirm").HasAttribute("disabled");

    private static StorageCellCandidateDto Cell(decimal cellId, string door, int column, int floor, int loadUnits = 0) =>
        new(cellId, door, 1m, "Automatic", column, floor, null, loadUnits);

    /// <summary>
    /// Item 54, decision 5 of the owner of 13/10: towards a relocator the question asks "Deposit it, or
    /// only bring it there?" - nothing chosen to begin with, and the change cannot be sent until the
    /// operator says it; the choice goes to the host with the change, the question and the outcome say
    /// it, and the row shows it. "Deposit it" also lets the operator choose the cell (decision 6).
    /// </summary>
    [Theory]
    [InlineData("deposit")]
    [InlineData("stay")]
    public void TowardsARelocatorTheQuestionAsksWhetherToDepositItOrOnlyBringItThere(string option)
    {
        AWarehouseRelocatedByTheLift();
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, C);
        AskToSetDestination(cut);

        Assert.Equal("Deposit it, or only bring it there?", cut.Find("#drawio-mission-destination-relocator-question").TextContent);
        Assert.Equal(["drawio-mission-destination-relocator-deposit", "drawio-mission-destination-relocator-stay"],
            cut.FindAll("#drawio-mission-destination-relocator input[type='radio']").Select(radio => radio.Id));
        Assert.All(cut.FindAll("#drawio-mission-destination-relocator input[type='radio']"), radio => Assert.False(radio.HasAttribute("checked")));
        Assert.True(ConfirmDisabled(cut));
        Assert.Empty(cut.FindAll("#drawio-mission-destination-cell"));
        Assert.Equal("Send LU-001 to Lift?", Question(cut));

        ChooseArrival(cut, option);

        Assert.False(ConfirmDisabled(cut));
        var (question, outcome, arrival, column) = option == "stay"
            ? ("Send LU-001 to Lift? It stays at Lift: nothing else is done with it.",
               "LU-001 is now bound for Lift, where it stays: the host took it.", MissionRelocatorArrival.Stay, "Stay")
            : ("Send LU-001 to Lift? At Lift the warehouse system is asked for a cell Lift reaches, and Lift deposits it there.",
               "LU-001 is now bound for Lift, where it is deposited in a cell the warehouse system gives: the host took it.",
               MissionRelocatorArrival.Deposit, "Deposit");
        Assert.Equal(question, Question(cut));
        Assert.Equal(option == "deposit", cut.FindAll("#drawio-mission-destination-cell").Count == 1);

        ConfirmDestination(cut);

        cut.WaitForAssertion(() => Assert.Equal(outcome, MoveOutcome(cut)));
        var (_, update) = Assert.Single(_host.MissionDestinationRequests);
        Assert.Equal(new MissionDestinationUpdate(C, MissionGatewayUpdate.Keep, arrival, null), update);
        cut.WaitForAssertion(() => Assert.Equal(column, CellText(cut, M1, nameof(MissionDto.RelocatorArrival))));
        Assert.Equal("Lift", Destination(cut, M1));
    }

    /// <summary>
    /// Towards a device that is neither a relocator nor a warehouse nothing more is asked, and the change
    /// goes as before: no relocator's choice, no cell.
    /// </summary>
    [Fact]
    public void TowardsAnyOtherDeviceNeitherTheRelocatorNorTheCellIsAsked()
    {
        AWarehouseRelocatedByTheLift();
        _host.Junctions = [Junction(B, A)];
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M2);
        PickTarget(cut, A);
        AskToSetDestination(cut);

        Assert.Empty(cut.FindAll("#drawio-mission-destination-relocator"));
        Assert.Empty(cut.FindAll("#drawio-mission-destination-cell"));
        Assert.False(ConfirmDisabled(cut));
        ConfirmDestination(cut);

        cut.WaitForAssertion(() => Assert.Equal("LU-002 is now bound for Shuttle 1: the host took it.", MoveOutcome(cut)));
        Assert.Equal(new MissionDestinationUpdate(A, MissionGatewayUpdate.Keep, null, null), Assert.Single(_host.MissionDestinationRequests).Update);
    }

    /// <summary>
    /// Item 54, decision 6 and answer 6: towards a warehouse the cell is the warehouse system's to choose
    /// to begin with, as before; "A cell I choose" reads the cells it proposes (the first 20, in its
    /// order), and the one picked from the list goes to the host by its door A code. The question names
    /// it, the outcome names it with the warehouse the row reads, and the row carries its door.
    /// </summary>
    [Fact]
    public void ACellChosenFromTheWarehouseSystemsListGoesToTheHostAndTheRowReadsIt()
    {
        AWarehouseRelocatedByTheLift();
        _host.DoorMappings.Add(new DoorMappingDto(1, S, "A1", 101m, "A", 0, false));
        _host.DoorMappings.Add(new DoorMappingDto(2, S, "A2", 102m, "A", 0, false));
        _host.StorageCells[S] = [Cell(101m, "A1", 3, 1, loadUnits: 1), Cell(102m, "A2", 4, 2)];
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, S);
        AskToSetDestination(cut);

        Assert.Empty(cut.FindAll("#drawio-mission-destination-relocator"));
        Assert.Equal(["drawio-mission-destination-cell-warehouse", "drawio-mission-destination-cell-precise"],
            cut.FindAll("#drawio-mission-destination-cell input[type='radio']").Select(radio => radio.Id));
        Assert.True(cut.Find("#drawio-mission-destination-cell-warehouse").HasAttribute("checked"));
        Assert.Empty(cut.FindAll("#drawio-mission-destination-cell-code"));
        Assert.Empty(_host.StorageCellsRequests);

        ChooseCell(cut, "precise");

        cut.WaitForAssertion(() => Assert.Equal(
            "The warehouse system proposes 2 cells for LU-001 in Warehouse 1, in its order, or write another one's door A code.",
            cut.Find("#drawio-mission-destination-cell-listed").TextContent));
        Assert.Equal((M1, S), Assert.Single(_host.StorageCellsRequests));
        Assert.Equal(["(choose a cell)", "A1 - Automatic, column 3, floor 1, 1 load unit in it", "A2 - Automatic, column 4, floor 2, 0 load units in it"],
            cut.FindAll("#drawio-mission-destination-cell-list option").Select(o => o.TextContent));
        Assert.True(ConfirmDisabled(cut));

        Act(cut, () => cut.Find("#drawio-mission-destination-cell-list").Change("A2"));

        Assert.Equal("A2", cut.Find("#drawio-mission-destination-cell-code").GetAttribute("value"));
        Assert.Equal("Send LU-001 into the cell A2 of Warehouse 1? The warehouse system checks that the cell is free and suits it, "
                     + "and reserves it; otherwise the change is refused with the reason.", Question(cut));
        Assert.False(ConfirmDisabled(cut));
        // Back to the warehouse system and to the operator's cell again: the list is not read twice.
        ChooseCell(cut, "warehouse");
        Assert.Equal("Send LU-001 to Warehouse 1?", Question(cut));
        ChooseCell(cut, "precise");
        Assert.Single(_host.StorageCellsRequests);

        ConfirmDestination(cut);

        cut.WaitForAssertion(() => Assert.Equal("LU-001 is now bound for the cell A2 in Warehouse 1: the host took it.", MoveOutcome(cut)));
        Assert.Equal(new MissionDestinationUpdate(S, MissionGatewayUpdate.Keep, null, "A2"), Assert.Single(_host.MissionDestinationRequests).Update);
        Assert.Equal("A2", CellText(cut, M1, nameof(MissionDto.StorageDoorCode)));
        Assert.Equal("Warehouse 1", Destination(cut, M1));
    }

    /// <summary>
    /// Item 54, decision 6: a cell that is not in the list is written by its code, which goes to the host
    /// trimmed - towards the warehouse, and through its relocator once "Deposit it" is chosen, where the
    /// precise cell goes alone (the host refuses it with the relocator's choice: the relocator that serves
    /// the cell deposits it).
    /// </summary>
    [Theory]
    [InlineData("warehouse")]
    [InlineData("relocator")]
    public void ACellCodeWrittenByHandGoesToTheHostTrimmed(string through)
    {
        AWarehouseRelocatedByTheLift();
        _host.DoorMappings.Add(new DoorMappingDto(1, S, "A9", 109m, "A", 0, false));
        var target = through == "warehouse" ? S : C;
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, target);
        AskToSetDestination(cut);
        if (through == "relocator") ChooseArrival(cut, "deposit");
        ChooseCell(cut, "precise");

        var where = through == "warehouse" ? "Warehouse 1" : "Lift";
        cut.WaitForAssertion(() => Assert.Equal(
            $"The warehouse system proposes no cell for LU-001 in {where}. Write the cell's door A code to choose one.",
            cut.Find("#drawio-mission-destination-cell-listed").TextContent));
        Assert.Empty(cut.FindAll("#drawio-mission-destination-cell-list"));
        Assert.True(ConfirmDisabled(cut));
        Act(cut, () => cut.Find("#drawio-mission-destination-cell-code").Input("   "));
        Assert.True(ConfirmDisabled(cut));

        Act(cut, () => cut.Find("#drawio-mission-destination-cell-code").Input(" A9 "));

        Assert.Equal(through == "warehouse"
            ? "Send LU-001 into the cell A9 of Warehouse 1? The warehouse system checks that the cell is free and suits it, and reserves it; otherwise the change is refused with the reason."
            : "Send LU-001 into the cell A9, deposited by Lift? The warehouse system checks that the cell is free and suits it, and reserves it; otherwise the change is refused with the reason.",
            Question(cut));
        ConfirmDestination(cut);

        cut.WaitForAssertion(() => Assert.Equal("LU-001 is now bound for the cell A9 in Warehouse 1: the host took it.", MoveOutcome(cut)));
        Assert.Equal(new MissionDestinationUpdate(target, MissionGatewayUpdate.Keep, null, "A9"), Assert.Single(_host.MissionDestinationRequests).Update);
        Assert.Equal("", CellText(cut, M1, nameof(MissionDto.RelocatorArrival)));
    }

    /// <summary>
    /// Item 54, decision 6 and answer 7: every refusal of the precise cell in plain words, from the
    /// field of the 422 - not the host's Italian, which is behind the "?" - and the mission as it was.
    /// </summary>
    [Theory]
    [InlineData("unknownDoor", "ZZ", "no cell has the door code ZZ")]
    [InlineData("notADoorA", "B7", "B7 is the code of a door B: a cell is chosen by its door A code")]
    [InlineData("notInTheChoice", "W1", "the cell W1 is not in Warehouse 1")]
    [InlineData("cellNotAvailable", "A1", "the warehouse system says the cell A1 is not free, or does not suit the load unit")]
    [InlineData("unknownLoadUnit", "A1", "the warehouse system does not know its load unit")]
    public void EveryRefusalOfThePreciseCellIsSaidInPlainWordsAndTheRowStays(string refusal, string code, string why)
    {
        AWarehouseRelocatedByTheLift();
        _host.Items.Add(Item(W2, "Warehouse 2", 1200, DeviceTypes.Storage));
        _host.DoorMappings.Add(new DoorMappingDto(1, S, "A1", 101m, "A", 0, false));
        _host.DoorMappings.Add(new DoorMappingDto(2, S, "B7", 101m, "B", 0, false));
        _host.DoorMappings.Add(new DoorMappingDto(3, W2, "W1", 201m, "A", 0, false));
        if (refusal is "cellNotAvailable" or "unknownLoadUnit") _host.CellsTheWarehouseRefuses["A1"] = refusal;
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, S);
        AskToSetDestination(cut);
        ChooseCell(cut, "precise");
        Act(cut, () => cut.Find("#drawio-mission-destination-cell-code").Input(code));
        ConfirmDestination(cut);

        cut.WaitForAssertion(() => Assert.Equal($"LU-001 was NOT sent to Warehouse 1: {why}. It still has no destination.", MoveOutcome(cut)));
        Assert.StartsWith("the host answered ", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-mission-move-outcome"));
        Assert.Equal(code, Assert.Single(_host.MissionDestinationRequests).Update.CellDoorCode);
        Assert.Equal("", Destination(cut, M1));
        Assert.Equal(1, MissionReads);
    }

    /// <summary>
    /// The precise cell refused through the relocator: the cell of a warehouse it does not serve is said
    /// as one it does not serve.
    /// </summary>
    [Fact]
    public void ACellTheRelocatorDoesNotServeIsSaidSo()
    {
        AWarehouseRelocatedByTheLift();
        _host.Items.Add(Item(W2, "Warehouse 2", 1200, DeviceTypes.Storage));
        _host.DoorMappings.Add(new DoorMappingDto(1, W2, "W1", 201m, "A", 0, false));
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, C);
        AskToSetDestination(cut);
        ChooseArrival(cut, "deposit");
        ChooseCell(cut, "precise");
        Act(cut, () => cut.Find("#drawio-mission-destination-cell-code").Input("W1"));
        ConfirmDestination(cut);

        cut.WaitForAssertion(() => Assert.Equal("LU-001 was NOT sent to Lift: the cell W1 is not one Lift serves. It still has no destination.", MoveOutcome(cut)));
    }

    /// <summary>
    /// The two refusals of item 54 the page does not provoke itself - the plant changed under it, or a
    /// request not well formed - are said in words too, from their titles.
    /// </summary>
    [Theory]
    [InlineData(MissionDestinationProblems.NotARelocatorTitle, "Lift is not the relocator of any warehouse, so it cannot be told to deposit it or to keep it")]
    [InlineData(MissionDestinationProblems.InvalidDestinationChoiceTitle, "the choice sent with it is not valid")]
    public void TheRelocatorsRefusalsAreSaidInPlainWords(string title, string why)
    {
        AWarehouseRelocatedByTheLift();
        _host.FailRoute = "mission-destination";
        _host.FailureStatus = HttpStatusCode.BadRequest;
        _host.FailureBody = JsonSerializer.Serialize(new { type = "https://tools.ietf.org/html/rfc9110", title, status = 400, detail = "parole dell'host" });
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, C);
        AskToSetDestination(cut);
        ChooseArrival(cut, "stay");
        ConfirmDestination(cut);

        cut.WaitForAssertion(() => Assert.Equal($"LU-001 was NOT sent to Lift: {why}. It still has no destination.", MoveOutcome(cut)));
    }

    /// <summary>
    /// The list that cannot be read is said in plain words - the mission gone, the warehouse system that
    /// does not answer, nothing to ask it, an answer the page does not know - and the code can still be
    /// written: the cell is the operator's to choose.
    /// </summary>
    [Theory]
    [InlineData("mission gone", "The cells could not be listed: LU-001 is no longer active on the host (it ended or was removed).")]
    [InlineData("warehouse", "The cells could not be listed: the warehouse system does not answer. Write the cell's door A code to choose one.")]
    [InlineData("nothing to ask", "The cells could not be listed: there is nothing to ask the warehouse system for this load unit in Warehouse 1 (the host says why). Write the cell's door A code to choose one.")]
    [InlineData("server error", "The cells could not be read from the host because the host answered with an error. Write the cell's door A code to choose one.")]
    public void ACellListThatCannotBeReadIsSaidSoAndTheCodeCanStillBeWritten(string failure, string expected)
    {
        AWarehouseRelocatedByTheLift();
        _host.DoorMappings.Add(new DoorMappingDto(1, S, "A9", 109m, "A", 0, false));
        switch (failure)
        {
            case "warehouse":
                _host.WarehouseUnreachable = true;
                break;
            case "nothing to ask":
                _host.FailRoute = "mission-storage-cells";
                _host.FailureStatus = HttpStatusCode.UnprocessableEntity;
                _host.FailureBody = JsonSerializer.Serialize(new
                {
                    type = "https://tools.ietf.org/html/rfc9110", title = StorageCellCandidatesProblems.NoStorageCellTitle, status = 422, detail = "parole dell'host",
                });
                break;
            case "server error":
                _host.FailRoute = "mission-storage-cells";
                _host.FailureStatus = HttpStatusCode.InternalServerError;
                break;
        }

        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, S);
        AskToSetDestination(cut);
        if (failure == "mission gone") _host.Missions.RemoveAt(0);
        ChooseCell(cut, "precise");

        cut.WaitForAssertion(() => Assert.Equal(expected, cut.Find("#drawio-mission-destination-cell-error").TextContent.Trim()));
        Assert.Empty(cut.FindAll("#drawio-mission-destination-cell-list"));
        if (failure == "mission gone") return;

        _host.WarehouseUnreachable = false;
        Act(cut, () => cut.Find("#drawio-mission-destination-cell-code").Input("A9"));
        ConfirmDestination(cut);
        cut.WaitForAssertion(() => Assert.Equal("LU-001 is now bound for the cell A9 in Warehouse 1: the host took it.", MoveOutcome(cut)));
    }

    /// <summary>
    /// A list that arrives for a question no longer open is not shown under the next one: the question
    /// asked again reads its own list, and the late answer of the first one does not replace it.
    /// </summary>
    [Fact]
    public async Task AListThatArrivesAfterTheQuestionClosedIsNotKept()
    {
        AWarehouseRelocatedByTheLift();
        _host.StorageCells[S] = [Cell(101m, "A1", 3, 1)];
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, S);
        AskToSetDestination(cut);
        var first = _host.StorageCellsGate = new TaskCompletionSource();
        // The gesture's own task: it ends when the answer it waits for has landed, kept or dropped.
        Task firstGesture = Task.CompletedTask;
        await cut.InvokeAsync(() =>
        {
            firstGesture = cut.Find("#drawio-mission-destination-cell-precise").ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });
        });
        await _host.StorageCellsGateReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cut.WaitForAssertion(() => Assert.Equal("Asking the warehouse system for the cells...", cut.Find("#drawio-mission-destination-cell-reading").TextContent));

        Act(cut, () => cut.Find("#drawio-mission-destination-cancel").Click());
        AskToSetDestination(cut);
        _host.StorageCellsGate = null;
        _host.StorageCells[S] = [Cell(101m, "A1", 3, 1), Cell(102m, "A2", 4, 2)];
        ChooseCell(cut, "precise");
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("#drawio-mission-destination-cell-list option").Count));
        Assert.Equal(2, _host.StorageCellsRequests.Count);

        // The first question's answer lands now, and the gesture that asked for it ends: dropped.
        Assert.False(firstGesture.IsCompleted);
        first.SetResult();
        await firstGesture.WaitAsync(TimeSpan.FromSeconds(10));
        await cut.InvokeAsync(() => { });

        Assert.Equal(["(choose a cell)", "A1 - Automatic, column 3, floor 1, 0 load units in it", "A2 - Automatic, column 4, floor 2, 0 load units in it"],
            cut.FindAll("#drawio-mission-destination-cell-list option").Select(o => o.TextContent));
    }

    /// <summary>
    /// Item 54, decision 4 with decision 6: a precise cell asked during a step waits for its end like any
    /// change, and the outcome says where it goes.
    /// </summary>
    [Fact]
    public void APreciseCellAskedDuringAStepIsAppliedAtTheEndOfTheMovement()
    {
        AWarehouseRelocatedByTheLift();
        _host.DoorMappings.Add(new DoorMappingDto(1, S, "A9", 109m, "A", 0, false));
        _host.MissionsWithAStepInFlight.Add(M1);
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, S);
        AskToSetDestination(cut);
        ChooseCell(cut, "precise");
        Act(cut, () => cut.Find("#drawio-mission-destination-cell-code").Input("A9"));
        ConfirmDestination(cut);

        cut.WaitForAssertion(() => Assert.Equal(
            "LU-001 is moving: it is sent into the cell A9 at the end of the movement - the change is applied at the end of the movement.",
            MoveOutcome(cut)));
    }

    /// <summary>
    /// A precise cell chosen through the relocator is a change towards the warehouse of the cell
    /// (MissionGateway.ChangeDestinationAsync, item 54): the question says what it does to an extraction's
    /// order as it does towards a warehouse - picked up, the order is reopened; still in its cell, it is a
    /// change of cell, and a refusal reads the list again, since it may have closed the mission.
    /// </summary>
    [Theory]
    [InlineData("picked up")]
    [InlineData("in its cell")]
    public void APreciseCellThroughTheRelocatorSaysWhatItDoesToTheExtractionsOrder(string where)
    {
        AWarehouseRelocatedByTheLift();
        _host.DoorMappings.Add(new DoorMappingDto(1, S, "A9", 109m, "A", 0, false));
        _host.Missions[0] = where == "picked up"
            ? _host.Missions[0] with { MissionType = MissionTypes.Extraction }
            : _host.Missions[0] with { MissionType = MissionTypes.Extraction, PositionItemId = S, ExtractionQueueId = 5m };
        var cut = RenderPage();
        OpenTheList(cut);
        Choose(cut, M1);
        PickTarget(cut, C);
        AskToSetDestination(cut);
        ChooseArrival(cut, "deposit");
        Assert.Equal("Send LU-001 to Lift? At Lift the warehouse system is asked for a cell Lift reaches, and Lift deposits it there.", Question(cut));
        ChooseCell(cut, "precise");
        Act(cut, () => cut.Find("#drawio-mission-destination-cell-code").Input("A9"));

        const string cell = "Send LU-001 into the cell A9, deposited by Lift? The warehouse system checks that the cell is free and suits it, and reserves it; otherwise the change is refused with the reason.";
        Assert.Equal(where == "picked up"
            ? $"{cell} It was picked up for an order: the warehouse system is told to reopen that order, and an order already shipped stays as it is."
            : $"{cell} It is still in its cell in Warehouse 1: this makes it a change of cell, and its order goes back free in the warehouse system."
              + " If the change is refused after the warehouse system was told, the mission ends and the load unit stays in its cell.",
            Question(cut));
        if (where == "picked up") return;

        // The refusal of a change of cell reads the list again: it may have closed the mission.
        _host.CellsTheWarehouseRefuses["A9"] = "cellNotAvailable";
        ConfirmDestination(cut);
        cut.WaitForAssertion(() => Assert.Equal(2, MissionReads));
        Assert.StartsWith("LU-001 was NOT sent to Lift: the warehouse system says the cell A9 is not free", MoveOutcome(cut));
    }
}
