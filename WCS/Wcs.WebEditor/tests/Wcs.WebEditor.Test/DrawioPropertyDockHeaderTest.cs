using System.Text.RegularExpressions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Drawio.Components;
using Wcs.WebEditor.Drawio.Coordination;
using Wcs.WebEditor.Pages;
using Wcs.WebEditor.Session;
using Wcs.WebEditor.Wms;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// The heading of the properties panel (passata grafica, 2026-09-29): the selection line that used to
/// sit above the bar (#drawio-selection, same id, same words, same error class) is the panel's title,
/// with the selected device's type and the load unit on board under it, and the note that names the
/// device and says when the permit is asked for (#drawio-property-subject) behind the heading's "?".
/// Under the heading, the panel's actions in a row: Duplicate first, then the commands.
/// </summary>
public sealed class DrawioPropertyDockHeaderTest : PlantCanvasBunitContext
{
    private const string LiveModule = "./_content/Wcs.WebEditor/drawio-live.js";

    private static readonly Guid Carousel = Guid.Parse("cccccccc-0000-4000-8000-000000000003");

    /// <summary>The note as it was on screen until the passata grafica, copied from the source before it moved, for a device named "Carousel 3".</summary>
    private const string SubjectWords = "Carousel 3, as the host has it. Change what you need and press \"Save\": the edit permit is asked for then, not now, so looking at a device takes nothing away from anyone. If the write is refused, what you typed stays here.";

    /// <summary>The line of a selection that cannot be followed, as it was above the bar.</summary>
    private const string UnavailableWords = "What is selected inside the drawing cannot be read: drawing and saving still work, but this page cannot follow the selection. Reload the page.";

    private readonly FakePlantEventSource _source = new();
    private readonly DrawioWriteHandler _host = new();
    private readonly BunitJSModuleInterop _live;

    public DrawioPropertyDockHeaderTest()
    {
        _live = JSInterop.SetupModule(LiveModule);
        _live.Setup<string>("render", _ => true).SetResult("drawn");
    }

    // ---- The panel alone -----------------------------------------------------------------------

    [Fact]
    public void TheHeadingIsTheSelectionWithTheTypeAndTheLoadUnitOnBoardUnderIt()
    {
        var cut = RenderDock(SelectedCarousel(), loadUnit: "LU-7 (7)");

        var head = cut.Find("#drawio-property-dock > .drawio-property-head");
        Assert.Contains("drawio-property-head", head.ClassList);
        Assert.Equal("Selected: Carousel 3", cut.Find(".drawio-property-head #drawio-selection").TextContent);
        Assert.Equal(["drawio-selection"], cut.Find(".drawio-property-head #drawio-selection").ClassList);
        // The type in the words the panel's own DeviceType field shows (the description, not the
        // enum's name), and the load unit as the device list's column shows it.
        Assert.Equal("Circular Distributor", cut.Find(".drawio-property-head #drawio-property-type").TextContent);
        Assert.Equal("Load unit: LU-7 (7)", cut.Find(".drawio-property-head #drawio-property-load-unit").TextContent);
    }

    /// <summary>
    /// The panel the right-click menu's "Properties" puts the focus in (#drawio-property-panel) is named
    /// after its device: the name is in the heading above it, outside the panel, and the note that said
    /// it is behind the "?" - a screen reader landing in the panel would hear nothing of whose it is.
    /// </summary>
    [Fact]
    public void ThePanelTheMenuFocusesIsNamedAfterItsDevice()
    {
        var cut = RenderDock(SelectedCarousel(), loadUnit: null);

        var panel = cut.Find("#drawio-property-panel");
        Assert.Equal("region", panel.GetAttribute("role"));
        Assert.Equal("Properties of Carousel 3", panel.GetAttribute("aria-label"));
    }

    [Fact]
    public void ADeviceWithNothingOnBoardHasItsTypeAndNoLoadUnit()
    {
        var cut = RenderDock(SelectedCarousel(), loadUnit: null);

        Assert.Equal("Circular Distributor", cut.Find(".drawio-property-head #drawio-property-type").TextContent);
        Assert.Empty(cut.FindAll("#drawio-property-load-unit"));
    }

    [Fact]
    public void TheSubjectSentenceIsBehindTheHeadingsQuestionMarkWordForWord()
    {
        var cut = RenderDock(SelectedCarousel(), loadUnit: null);

        Assert.NotNull(cut.Find(".drawio-property-head").QuerySelector("button#drawio-property-help"));
        Assert.True(cut.Find("#drawio-property-help-body").HasAttribute("hidden"));
        var subject = Assert.Single(cut.FindAll("#drawio-property-subject"));
        Assert.NotNull(subject.Closest("#drawio-property-help-body"));
        Assert.Equal(SubjectWords, Regex.Replace(subject.TextContent, @"\s+", " ").Trim());

        cut.Find("#drawio-property-help").Click();
        Assert.False(cut.Find("#drawio-property-help-body").HasAttribute("hidden"));
    }

    /// <summary>Several cells, none a device the panel can show: the line says what is selected, and there is no type, no load unit and no note to open.</summary>
    [Fact]
    public void WithoutADeviceTheHeadingIsTheSelectionLineAlone()
    {
        var cut = RenderDock(EmptyState() with
        {
            SelectedCellIds = ["7", "8"],
            SelectionLine = "Selected: 2 cells (0 devices of the plant).",
        }, loadUnit: null);

        Assert.Equal("Selected: 2 cells (0 devices of the plant).", cut.Find("#drawio-property-dock > .drawio-property-head #drawio-selection").TextContent);
        Assert.Empty(cut.FindAll("#drawio-property-type"));
        Assert.Empty(cut.FindAll("#drawio-property-load-unit"));
        Assert.Empty(cut.FindAll("#drawio-property-help"));
        Assert.Single(cut.FindAll("#drawio-property-hint"));
    }

    [Fact]
    public void ASelectionThatCannotBeFollowedIsTheHeadingInItsErrorClass()
    {
        var cut = RenderDock(EmptyState() with { SelectedCellIds = [Carousel.ToString()], SelectionUnavailable = true }, loadUnit: null);

        var line = cut.Find("#drawio-property-dock > .drawio-property-head #drawio-selection");
        Assert.Equal(["drawio-selection", "drawio-selection-error"], line.ClassList);
        Assert.Equal(UnavailableWords, line.TextContent);
    }

    /// <summary>Before draw.io has said anything there is no line - not an empty one, and not "Nothing selected." - and so no heading.</summary>
    [Fact]
    public void BeforeDrawioSaysAnythingThereIsNoHeading()
    {
        var cut = RenderDock(EmptyState(), loadUnit: null);

        Assert.Empty(cut.FindAll("#drawio-selection"));
        Assert.Empty(cut.FindAll(".drawio-property-head"));
    }

    /// <summary>
    /// Under the heading, first in the panel: the actions, Duplicate and then the commands, and the
    /// properties after them. The order is what this test sees; that the actions lie in one row of
    /// buttons is the browser's to say (DrawioPropertyPanelTest.WithTheEngineRunningThePanelsActionsAreOneRowOfButtons).
    /// </summary>
    [Fact]
    public void UnderTheHeadingTheActionsComeFirstDuplicateThenTheCommandsThenTheProperties()
    {
        var state = SelectedCarousel() with { Config = new PlantItemConfigDto { ItemId = Carousel, Name = "Carousel 3" } };
        var cut = Render<DrawioPropertyDock>(p => p
            .Add(x => x.State, state)
            .Add(x => x.Items, [CarouselItem(DeviceTypes.CircularDistributor)])
            .Add(x => x.Commands, "<section id=\"drawio-commands\">commands</section>")
            .Add(x => x.Duplicate, "<section id=\"drawio-duplicate\">duplicate</section>"));

        var panel = cut.Find("#drawio-property-panel");
        var actions = panel.FirstElementChild!;
        Assert.Contains("drawio-property-actions", actions.ClassList);
        Assert.Equal(["drawio-duplicate", "drawio-commands"], actions.Children.Select(child => child.Id));
        Assert.Contains("property-panel", actions.NextElementSibling!.ClassList);
        Assert.Empty(panel.QuerySelectorAll("#drawio-cells, #drawio-mission-steps"));
    }

    // ---- On the page ---------------------------------------------------------------------------

    /// <summary>
    /// On the page the selection is the dock's heading and no longer a line above the bar; the type
    /// comes from the plant the page holds; and a load unit arriving on the selected device with the
    /// live channel shows in the heading, even when the device's status stays the same - the page
    /// renders for the selected device when its load unit changes, not only its status.
    /// </summary>
    [Fact]
    public async Task OnThePageTheSelectionIsTheDocksHeadingAndALoadUnitArrivingLiveShowsInIt()
    {
        _host.Items = [CarouselItem(DeviceTypes.GroupOfAutomationDevices)];
        _host.Configs[Carousel] = new PlantItemConfigDto
        {
            ItemId = Carousel,
            Name = "Carousel 3",
            DeviceType = (short)DeviceTypes.GroupOfAutomationDevices,
        };
        _host.ItemStatuses = [Status(Carousel, loaded: false)];
        var cut = RenderPage();
        cut.WaitForAssertion(() => Assert.False(cut.Find("#drawio-mode-toggle").HasAttribute("disabled")));

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([Carousel.ToString()]));

        var line = cut.Find("#drawio-selection");
        Assert.Equal("Selected: Carousel 3", line.TextContent);
        Assert.NotNull(line.Closest("#drawio-property-dock > .drawio-property-head"));
        Assert.DoesNotContain(cut.Find(".drawio-shell").Children, child => child.Id == "drawio-selection");
        Assert.Equal("Group Automation", cut.Find("#drawio-property-type").TextContent);
        Assert.Empty(cut.FindAll("#drawio-property-load-unit"));

        // The first frame, the device ready and empty, on the canvas and on the page.
        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());
        cut.WaitForAssertion(() => Assert.True(Drawn(Carousel)), TimeSpan.FromSeconds(10));

        _source.RaiseItemStatus(Status(Carousel, loaded: true));

        cut.WaitForAssertion(() => Assert.Equal("Load unit: LU-7 (7)", cut.Find("#drawio-property-load-unit").TextContent),
            TimeSpan.FromSeconds(10));
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private IRenderedComponent<DrawioPropertyDock> RenderDock(DrawioSelectionState state, string? loadUnit) =>
        Render<DrawioPropertyDock>(p => p
            .Add(x => x.State, state)
            .Add(x => x.Items, [CarouselItem(DeviceTypes.CircularDistributor)])
            .Add(x => x.LoadUnit, loadUnit));

    private IRenderedComponent<DrawioEditor> RenderPage()
    {
        var state = new EditorSessionState(new Uri("http://localhost:5580"), _host) { EventSourceForTesting = () => _source };
        state.UseKey("test-key");
        Services.AddSingleton(state);
        return Render<DrawioEditor>();
    }

    private bool Drawn(Guid itemId) =>
        _live.Invocations["render"].Any(call => ((string)call.Arguments[0]!).Contains(itemId.ToString()));

    private static PlantItemDto CarouselItem(DeviceTypes type) =>
        new(Carousel, "Carousel 3", type, null, null, null, ConnectionTypes.Test,
            default, 0, null, 0, 0, 0, 0, default!, 10, 10, 100, 100, 0);

    /// <summary>
    /// Busy, with or without a load unit on board: the status does not change between the two. Busy and
    /// not Ready: a device with no colour and no load unit is not in the frame sent to the canvas
    /// (PlantLiveState.Devices), and the first frame has to be seen there.
    /// </summary>
    private static ItemStatus Status(Guid id, bool loaded) =>
        new(id, "", "", "", "Carousel 3", DeviceStatusTypes.Busy, loaded ? LuLoadStatusTypes.Loaded : LuLoadStatusTypes.None,
            loaded ? "LU-7" : "", loaded ? 7m : 0m, Guid.Empty, Guid.Empty);

    private static DrawioSelectionState SelectedCarousel() => EmptyState() with
    {
        SelectedCellIds = [Carousel.ToString()],
        SelectionLine = "Selected: Carousel 3",
        ConfigItemId = Carousel,
        ConfigSubjectName = "Carousel 3",
    };

    private static DrawioSelectionState EmptyState() => new(
        SelectedCellIds: null,
        SelectionUnavailable: false,
        SelectionLine: "Nothing selected.",
        ConfigItemId: null,
        Config: null,
        ConfigError: null,
        ConfigSubjectName: "",
        ConfigWriteError: null,
        ConfigWritten: null,
        CellsKind: CellPanelKind.None,
        CellsMappings: null,
        WmsCells: null,
        AutomaticCells: null,
        CellsError: null,
        CellsErrorDetail: null,
        CellsNotice: null,
        CellsWriteError: null,
        CellsWritten: null,
        ReassociableCell: null,
        ReassociateTyped: "",
        ReassociateOutcome: null,
        ReassociateWentThrough: false,
        ReassociationCandidates: null,
        ReassociationCandidatesLine: "No candidates",
        ReassociateRound: 0,
        Generation: 0);
}
