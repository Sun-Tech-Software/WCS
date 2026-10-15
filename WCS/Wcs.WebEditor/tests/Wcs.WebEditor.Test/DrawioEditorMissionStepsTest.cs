using Bunit;
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
/// Compito 3, row 61: the mission steps that touch the selected device, in its panel, READ-ONLY -
/// the WinForms <c>FrmMISSION_STEPS</c> rule (<c>FrmMISSION_STEPS.cs:43-45</c>): every step whose
/// RunningNode, FromNodeId or ToNodeId is the device.
/// </summary>
public sealed class DrawioEditorMissionStepsTest : PlantCanvasBunitContext
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");
    private static readonly Guid C = Guid.Parse("cccccccc-0000-4000-8000-000000000003");
    private static readonly Guid M1 = Guid.Parse("11111111-0000-4000-8000-000000000001");
    private static readonly Guid M2 = Guid.Parse("22222222-0000-4000-8000-000000000002");
    private static readonly Guid M3 = Guid.Parse("33333333-0000-4000-8000-000000000003");
    private static readonly Guid M4 = Guid.Parse("44444444-0000-4000-8000-000000000004");

    private static readonly Guid Leaving = Guid.Parse("5a000000-0000-4000-8000-000000000001");
    private static readonly Guid Arriving = Guid.Parse("5b000000-0000-4000-8000-000000000002");
    private static readonly Guid Running = Guid.Parse("5c000000-0000-4000-8000-000000000003");
    private static readonly Guid Elsewhere = Guid.Parse("5d000000-0000-4000-8000-000000000004");
    private static readonly Guid AtoB = Guid.Parse("1a2b0000-0000-4000-8000-00000000000a");

    private readonly DrawioWriteHandler _host = new();
    private readonly FakePlantEventSource _source = new();

    public DrawioEditorMissionStepsTest()
    {
        _host.Items = [Item(A, "Shuttle 1", 10), Item(B, "Shuttle 2", 300), Item(C, "Lift", 600)];
        foreach (var id in new[] { A, B, C })
            _host.Configs[id] = new PlantItemConfigDto { ItemId = id, Name = "d", DeviceType = (short)DeviceTypes.Loading };
        _host.Missions = [TestMissions.Mission(M1, A), TestMissions.Mission(M2, C), TestMissions.Mission(M3, B)];
        _host.MissionSteps[M1] = [TestMissions.Step(Leaving, M1, from: A, to: B)];
        _host.MissionSteps[M2] = [TestMissions.Step(Arriving, M2, from: C, to: A)];
        _host.Junctions = [new JunctionDto(AtoB, A, B, 99)];
        _host.MissionSteps[M3] = [TestMissions.Step(Running, M3, from: B, to: C, running: A), TestMissions.Step(Elsewhere, M3, from: B, to: C)];
    }

    private static PlantItemDto Item(Guid id, string name, double x) =>
        new(id, name, DeviceTypes.Loading, null, null, null, ConnectionTypes.Test,
            default, 0, null, 0, 0, 0, 0, default!, x, 10, 100, 100, 0);

    private IRenderedComponent<DrawioEditor> RenderPage()
    {
        JSInterop.SetupModule("./_content/Wcs.WebEditor/drawio-bridge.js");
        var state = new EditorSessionState(new Uri("http://localhost:5580"), _host) { EventSourceForTesting = () => _source };
        state.UseKey("test-key");
        Services.AddSingleton(state);
        var cut = Render<DrawioEditor>();
        cut.WaitForAssertion(() => Assert.False(cut.Find("#drawio-mode-toggle").HasAttribute("disabled")));
        // The channel is open and the page holds its feed: a component rendered from here gets it.
        cut.WaitForAssertion(() => Assert.NotNull(cut.FindComponent<DrawioMissionList>().Instance.Feed));
        return cut;
    }

    private int StepReads => _host.Requests.Count(r => r == "GET /api/v1/missions/steps");

    private static async Task SelectAsync(IRenderedComponent<DrawioEditor> cut, Guid itemId) =>
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([itemId.ToString()]));

    private static IReadOnlyList<string> Steps(IRenderedComponent<DrawioEditor> cut) =>
        [.. cut.FindAll("#drawio-mission-steps-table tbody tr").Select(row => row.GetAttribute("data-step-id") ?? "").Order()];

    private static string Cell(IRenderedComponent<DrawioEditor> cut, Guid stepId, string column) =>
        cut.Find($"#drawio-mission-steps-table tr[data-step-id='{stepId}'] td[data-column='{column}']").TextContent.Trim();

    /// <summary>With no device selected there is no panel, and nothing is read.</summary>
    [Fact]
    public void NothingSelectedReadsNoStep()
    {
        var cut = RenderPage();

        Assert.Empty(cut.FindAll("#drawio-mission-steps"));
        Assert.Equal(0, StepReads);
    }

    /// <summary>
    /// The steps that leave from the device, arrive at it or run on it - and no other - with every
    /// property of the step as a column and the three Guid ends by device name.
    /// </summary>
    [Fact]
    public async Task TheStepsShownAreTheOnesThatTouchTheSelectedDevice()
    {
        var cut = RenderPage();

        await SelectAsync(cut, A);

        cut.WaitForAssertion(() => Assert.Equal(new[] { Leaving, Arriving, Running }.Select(id => id.ToString()).Order(), Steps(cut)));
        Assert.Equal(1, StepReads);
        var headers = cut.FindAll("#drawio-mission-steps-table thead th").Select(th => th.GetAttribute("data-column")).ToList();
        Assert.Equal(typeof(MissionStepDto).GetConstructors().Single().GetParameters().Select(p => p.Name), headers);
        Assert.Equal("Shuttle 1", Cell(cut, Leaving, nameof(MissionStepDto.FromNodeId)));
        Assert.Equal("Shuttle 2", Cell(cut, Leaving, nameof(MissionStepDto.ToNodeId)));
        Assert.Equal("Shuttle 1", Cell(cut, Running, nameof(MissionStepDto.RunningNode)));
        Assert.Equal(M1.ToString(), Cell(cut, Leaving, nameof(MissionStepDto.MissionId)));
    }

    /// <summary>
    /// The dedicated window owns the title and what the steps are about; the content does not repeat
    /// another heading under the title bar.
    /// </summary>
    [Fact]
    public async Task WhatTheStepsAreAboutIsBehindTheQuestionMarkOfTheirHeading()
    {
        var cut = RenderPage();

        await SelectAsync(cut, A);

        var head = cut.WaitForElement("#drawio-mission-steps-window > .drawio-window-title");
        Assert.NotNull(head.QuerySelector("button#drawio-mission-steps-window-help"));
        var scope = cut.Find("#drawio-mission-steps-window-help-body p");
        Assert.True(cut.Find("#drawio-mission-steps-window-help-body").HasAttribute("hidden"));
        Assert.Equal("The steps of the active missions that touch this device — running on it, leaving from it or arriving at it — as the host has them. Read-only.",
            System.Text.RegularExpressions.Regex.Replace(scope.TextContent, @"\s+", " ").Trim());
        Assert.Empty(cut.FindAll("#drawio-mission-steps .drawio-mission-steps-head"));
    }

    [Fact]
    public async Task TheWindowFiltersStepsAcrossTheirVisibleFields()
    {
        var cut = RenderPage();
        await SelectAsync(cut, A);
        cut.WaitForAssertion(() => Assert.Equal(3, Steps(cut).Count));

        await cut.Find("#drawio-mission-steps-filter").InputAsync(M1.ToString());

        Assert.Equal([Leaving.ToString()], Steps(cut));
    }

    [Fact]
    public async Task ADeviceNoStepTouchesSaysSo()
    {
        _host.MissionSteps.Clear();
        var cut = RenderPage();

        await SelectAsync(cut, B);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#drawio-mission-steps-none")));
    }

    /// <summary>
    /// A new selection reads again, and the answer for the device selected before - still on its way
    /// when the selection moved - does not land on the new one's panel.
    /// </summary>
    [Fact]
    public async Task AnAnswerForTheDeviceSelectedBeforeDoesNotLandOnTheNewOne()
    {
        var cut = RenderPage();
        _host.AllMissionStepsGate = new TaskCompletionSource();
        await SelectAsync(cut, A);
        await _host.AllMissionStepsGateReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await SelectAsync(cut, B);
        cut.WaitForAssertion(() => Assert.Equal(2, StepReads));
        _host.AllMissionStepsGate.SetResult();

        cut.WaitForAssertion(() => Assert.Equal(new[] { Leaving, Running, Elsewhere }.Select(id => id.ToString()).Order(), Steps(cut)));
    }

    /// <summary>
    /// A mission event that touches the device - a step shown here, a mission on, from or to it, or
    /// any new step - reads the steps again; one that does not, does not. The event that does not is
    /// sent FIRST: the read the second one causes is the proof that the first went by.
    /// </summary>
    [Fact]
    public async Task AMissionEventReadsTheStepsAgainOnlyWhenItTouchesTheDevice()
    {
        var cut = RenderPage();
        await SelectAsync(cut, A);
        cut.WaitForAssertion(() => Assert.Equal(3, Steps(cut).Count));

        _source.RaiseMission(TestMissions.Mission(M4, C, destination: B), MissionChange.PositionChanged);
        _host.MissionSteps.Remove(M1);
        _source.RaiseMission(TestMissions.Mission(M1, B), MissionChange.PositionChanged);

        cut.WaitForAssertion(() => Assert.Equal(new[] { Arriving, Running }.Select(id => id.ToString()).Order(), Steps(cut)));
        Assert.Equal(2, StepReads);

        _source.RaiseMission(TestMissions.Mission(M4, C, destination: A), MissionChange.Started);
        cut.WaitForAssertion(() => Assert.Equal(3, StepReads));
        _source.RaiseMission(TestMissions.Mission(M4, C), MissionChange.StepChanged);
        cut.WaitForAssertion(() => Assert.Equal(4, StepReads));
    }

    /// <summary>At the reconnection the steps are read again: what went by while the channel was down does not come back.</summary>
    [Fact]
    public async Task AReconnectionReadsTheStepsAgain()
    {
        var cut = RenderPage();
        await SelectAsync(cut, A);
        cut.WaitForAssertion(() => Assert.Equal(3, Steps(cut).Count));

        _source.RaiseReconnecting();
        _host.MissionSteps.Clear();
        _source.RaiseReconnected();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#drawio-mission-steps-none")));
    }

    /// <summary>
    /// A step starting or finishing raises no mission event; the device's own live status is what
    /// moves then, and it reads the steps again.
    /// </summary>
    [Fact]
    public async Task AChangeOfTheDevicesLiveStatusReadsTheStepsAgain()
    {
        var cut = RenderPage();
        await SelectAsync(cut, A);
        cut.WaitForAssertion(() => Assert.Equal(3, Steps(cut).Count));

        _host.MissionSteps[M1] = [TestMissions.Step(Leaving, M1, from: A, to: B, status: ResultCodeTypes.Success)];
        _source.RaiseItemStatus(new ItemStatus(A, "", "", "", "Shuttle 1", DeviceStatusTypes.Moving, LuLoadStatusTypes.None,
            "", 0m, Guid.Empty, Guid.Empty));

        cut.WaitForAssertion(() => Assert.Equal(nameof(ResultCodeTypes.Success), Cell(cut, Leaving, nameof(MissionStepDto.StepStatus))));
        Assert.Equal(2, StepReads);
    }

    /// <summary>
    /// A read that fails says so in the panel, and the rest of the panel is untouched. Row 37 of the
    /// queue: the why in words, and the host's answer behind the line's "?" - BrokenReads answers an
    /// empty 500 - instead of the client's "L'API del motore ha risposto 500: " in the sentence.
    /// </summary>
    [Fact]
    public async Task AStepReadThatFailsIsSaidSo()
    {
        _host.BrokenReads.Add("all-mission-steps");
        var cut = RenderPage();

        await SelectAsync(cut, A);

        cut.WaitForAssertion(() => Assert.Equal(
            "The mission steps could not be read from the host because the host answered with an error.",
            cut.Find("#drawio-mission-steps-error").TextContent));
        Assert.Equal("the host answered 500 with an empty body", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-mission-steps-error"));
    }

    // ---- Fix round 1 ------------------------------------------------------------------------------

    /// <summary>
    /// The render rule, like the lists': a render of the page that changes nothing the steps show
    /// (a section of the strip opened) does not render them again, and neither does a live frame
    /// that does not move this device's status. The frame is known to have gone by through a
    /// subscriber of the feed's own FrameReady added AFTER the panel's, and one more turn of the
    /// renderer's dispatcher, where the panel's handler queued its work.
    /// </summary>
    [Fact]
    public async Task WhatDoesNotChangeTheStepsDoesNotRenderThem()
    {
        var cut = RenderPage();
        await SelectAsync(cut, A);
        cut.WaitForAssertion(() => Assert.Equal(3, Steps(cut).Count));
        var steps = cut.FindComponent<DrawioMissionSteps>();
        var renders = steps.RenderCount;
        var pageRenders = cut.RenderCount;

        await cut.InvokeAsync(() => cut.Find("#drawio-junction-list-toggle").Click());
        Assert.True(cut.RenderCount > pageRenders, "The page did not render for the section.");
        Assert.Equal(renders, steps.RenderCount);

        var feed = cut.FindComponent<DrawioMissionList>().Instance.Feed!;
        var framed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        feed.FrameReady += () => framed.TrySetResult();
        _source.RaiseItemStatus(new ItemStatus(B, "", "", "", "Shuttle 2", DeviceStatusTypes.Moving, LuLoadStatusTypes.None,
            "", 0m, Guid.Empty, Guid.Empty));
        await framed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cut.InvokeAsync(() => { });

        Assert.Equal(renders, steps.RenderCount);
        Assert.Equal(1, StepReads);
    }

    /// <summary>
    /// FrmMISSION_STEPS.cs:36 opens only on a node whose diagram type is Automation - which in the
    /// WinForms is every PLANT DEVICE ("ITEM", DiagramManager.ReadDiagramType), whatever its device
    /// type, as against a junction or a drawing-only shape. Here that is a cell the panel knows as a
    /// device (DrawioSelectionCoordinator.DeviceOf): a junction's arrow or a shape that is no device
    /// gets no steps and reads none, and a device of any type gets them (the devices of this class
    /// are Loading, not Automation - TheStepsShownAreTheOnesThatTouchTheSelectedDevice).
    /// </summary>
    [Fact]
    public async Task OnlyAPlantDeviceHasMissionSteps()
    {
        var cut = RenderPage();

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([AtoB.ToString()]));
        await cut.InvokeAsync(() => { });
        Assert.Empty(cut.FindAll("#drawio-mission-steps"));
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged(["a-drawing-only-shape"]));
        await cut.InvokeAsync(() => { });
        Assert.Empty(cut.FindAll("#drawio-mission-steps"));
        Assert.Equal(0, StepReads);

        await SelectAsync(cut, B);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#drawio-mission-steps-table")));
    }
}
