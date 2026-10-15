using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Drawio.Components;
using Wcs.WebEditor.Pages;
using Wcs.WebEditor.Session;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// Compito 2, row 59: the junction list of the draw.io page, READ-ONLY like the WinForms one
/// (<c>FrmJUNCTIONS_LIST</c>: <c>AddColumnsToGrid(..., false)</c>), with a filter, and a row that
/// selects its arrow on the drawing.
/// </summary>
public sealed class DrawioEditorJunctionListTest : PlantCanvasBunitContext
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");
    private static readonly Guid C = Guid.Parse("cccccccc-0000-4000-8000-000000000003");
    private static readonly Guid AtoB = Guid.Parse("11111111-0000-4000-8000-00000000000a");
    private static readonly Guid BtoC = Guid.Parse("22222222-0000-4000-8000-00000000000b");

    private readonly DrawioWriteHandler _host = new();
    private BunitJSModuleInterop _bridge = null!;

    public DrawioEditorJunctionListTest()
    {
        _host.Items = [Item(A, "Inbound", 10), Item(B, "Lift", 300), Item(C, "Outbound", 600)];
        _host.Junctions = [new JunctionDto(AtoB, A, B, 99), new JunctionDto(BtoC, B, C, 42)];
    }

    private static PlantItemDto Item(Guid id, string name, double x) =>
        new(id, name, DeviceTypes.Automation, null, null, null, ConnectionTypes.Test,
            default, 0, null, 0, 0, 0, 0, default!, x, 10, 100, 100, 0);

    private IRenderedComponent<DrawioEditor> RenderPage(string selectCellAnswer = "ok")
    {
        _bridge = JSInterop.SetupModule("./_content/Wcs.WebEditor/drawio-bridge.js");
        _bridge.Setup<string?>("selectCell", _ => true).SetResult(selectCellAnswer);
        var state = new EditorSessionState(new Uri("http://localhost:5580"), _host);
        state.UseKey("test-key");
        Services.AddSingleton(state);
        var cut = Render<DrawioEditor>();
        // The plant is in once the mode switch is enabled (DrawioEditorModeSwitchTest's own wait).
        cut.WaitForAssertion(() => Assert.False(cut.Find("#drawio-mode-toggle").HasAttribute("disabled")));
        // The list reads nothing of its own, but it renders its rows only while its section is open.
        Act(cut, () => cut.Find("#drawio-junction-list-toggle").Click());
        return cut;
    }

    /// <summary>Finds and triggers in ONE step on the renderer's dispatcher: see DrawioEditorDeviceListTest.Act.</summary>
    private static void Act(IRenderedComponent<DrawioEditor> cut, Action gesture) =>
        cut.InvokeAsync(gesture).GetAwaiter().GetResult();

    private static IReadOnlyList<string> Rows(IRenderedComponent<DrawioEditor> cut) =>
        [.. cut.FindAll("#drawio-junction-list-table tbody tr").Select(row => row.GetAttribute("data-junction-id") ?? "")];

    private static string CellText(IRenderedComponent<DrawioEditor> cut, Guid junctionId, string column) =>
        cut.Find($"#drawio-junction-list-table tr[data-junction-id='{junctionId}'] td[data-column='{column}']").TextContent.Trim();

    [Fact]
    public void TheGridShowsTenJunctionsAtATimeAndKeepsItsControlsOutsideTheScrollableRows()
    {
        for (var number = 3; number <= 24; number++)
        {
            _host.Items.Add(Item(NumberedDevice(number), $"Node {number:00}", number * 100));
        }
        for (var number = 3; number <= 23; number++)
            _host.Junctions.Add(new JunctionDto(NumberedJunction(number), NumberedDevice(number), NumberedDevice(number + 1), number));

        var cut = RenderPage();

        Assert.Equal(10, Rows(cut).Count);
        Assert.Equal("1–10 of 23", cut.Find("#drawio-junction-list-page-summary").TextContent.Trim());
        Assert.Contains("drawio-data-grid-scroll", cut.Find("#drawio-junction-list-table").ParentElement!.ClassList);
        Assert.NotNull(cut.Find("#drawio-junction-list-page-summary").Closest("footer"));

        Act(cut, () => cut.Find("#drawio-junction-list-next").Click());

        Assert.Equal(10, Rows(cut).Count);
        Assert.Equal("11–20 of 23", cut.Find("#drawio-junction-list-page-summary").TextContent.Trim());

        Act(cut, () => cut.Find("#drawio-junction-list-next").Click());

        Assert.Equal(3, Rows(cut).Count);
        Assert.Equal("21–23 of 23", cut.Find("#drawio-junction-list-page-summary").TextContent.Trim());
    }

    [Fact]
    public void FilteringAndSortingStartTheJunctionGridFromItsFirstPage()
    {
        for (var number = 3; number <= 14; number++)
        {
            _host.Items.Add(Item(NumberedDevice(number), $"Node {number:00}", number * 100));
        }
        for (var number = 3; number <= 13; number++)
            _host.Junctions.Add(new JunctionDto(NumberedJunction(number), NumberedDevice(number), NumberedDevice(number + 1), number));

        var cut = RenderPage();
        Act(cut, () => cut.Find("#drawio-junction-list-next").Click());

        Act(cut, () => cut.Find("#drawio-junction-list-table th[data-column='Priority']").Click());
        Assert.Equal("1–10 of 13", cut.Find("#drawio-junction-list-page-summary").TextContent.Trim());

        Act(cut, () => cut.Find("#drawio-junction-list-next").Click());
        Act(cut, () => cut.Find("#drawio-junction-list-filter").Input("Outbound"));

        Assert.Equal([BtoC.ToString()], Rows(cut));
        Assert.Equal("1 of 1", cut.Find("#drawio-junction-list-page-summary").TextContent.Trim());
        var filter = cut.Find("#drawio-junction-list-filter");
        Assert.Equal("search", filter.ParentElement!.QuerySelector(".drawio-icon")!.GetAttribute("data-icon"));
        Assert.Equal("Filter junctions", filter.GetAttribute("aria-label"));
    }

    [Fact]
    public async Task AJunctionSelectedOnTheDrawingBringsItsOffPageRowIntoView()
    {
        for (var number = 3; number <= 14; number++)
            _host.Items.Add(Item(NumberedDevice(number), $"Node {number:00}", number * 100));
        for (var number = 3; number <= 13; number++)
            _host.Junctions.Add(new JunctionDto(NumberedJunction(number), NumberedDevice(number), NumberedDevice(number + 1), number));

        var cut = RenderPage();
        var selected = NumberedJunction(13);
        Assert.DoesNotContain(selected.ToString(), Rows(cut));

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([selected.ToString()]));

        cut.WaitForAssertion(() => Assert.Equal("true",
            cut.Find($"#drawio-junction-list-table tr[data-junction-id='{selected}']").GetAttribute("aria-selected")));
        Assert.Equal("11–13 of 13", cut.Find("#drawio-junction-list-page-summary").TextContent.Trim());
    }

    /// <summary>One row per junction, its two ends by the devices' names, its priority - and nothing to type into.</summary>
    [Fact]
    public void EveryJunctionIsARowWithItsEndsByNameAndNothingEditable()
    {
        var cut = RenderPage();

        Assert.Equal(2, Rows(cut).Count);
        Assert.Equal("Inbound", CellText(cut, AtoB, "Source"));
        Assert.Equal("Lift", CellText(cut, AtoB, "Destination"));
        Assert.Equal("99", CellText(cut, AtoB, "Priority"));
        Assert.Equal("42", CellText(cut, BtoC, "Priority"));
        Assert.Empty(cut.FindAll("#drawio-junction-list-table input, #drawio-junction-list-table select"));
    }

    [Fact]
    public void TheFilterKeepsOnlyTheJunctionsThatMatch()
    {
        var cut = RenderPage();

        Act(cut, () => cut.Find("#drawio-junction-list-filter").Input("outbound"));

        Assert.Equal([BtoC.ToString()], Rows(cut));
    }

    private static Guid NumberedDevice(int number) =>
        Guid.Parse($"eeeeeeee-0000-4000-8000-{number:000000000000}");

    private static Guid NumberedJunction(int number) =>
        Guid.Parse($"ffffffff-0000-4000-8000-{number:000000000000}");

    /// <summary>Choosing a row selects the junction's arrow on the drawing, and the row of the arrow selected there is marked.</summary>
    [Fact]
    public async Task ChoosingARowSelectsTheArrowOnTheDrawing()
    {
        var cut = RenderPage();
        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());

        Act(cut, () => cut.Find($"#drawio-junction-list-table tr[data-junction-id='{BtoC}']").Click());

        cut.WaitForAssertion(() => Assert.Contains(_bridge.Invocations["selectCell"], call => (call.Arguments[0] as string) == BtoC.ToString()));
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([BtoC.ToString()]));
        cut.WaitForAssertion(() => Assert.Equal("true",
            cut.Find($"#drawio-junction-list-table tr[data-junction-id='{BtoC}']").GetAttribute("aria-selected")));
        Assert.Equal("false", cut.Find($"#drawio-junction-list-table tr[data-junction-id='{AtoB}']").GetAttribute("aria-selected"));
    }

    /// <summary>
    /// A row chosen while the drawing does not answer: why in the page's words, and what the page's
    /// script threw behind the line's "?" (row 37 of the queue, extended to the whole page on 10/10).
    /// </summary>
    [Fact]
    public async Task AGoToTheDrawingDoesNotAnswerKeepsTheFailureBehindTheQuestionMark()
    {
        var cut = RenderPage();
        _bridge.Setup<string?>("selectCell", _ => true).SetException(new Microsoft.JSInterop.JSDisconnectedException("il circuito non c'e' piu' (prova)"));
        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());

        Act(cut, () => cut.Find($"#drawio-junction-list-table tr[data-junction-id='{AtoB}']").Click());

        cut.WaitForAssertion(() => Assert.EndsWith(": the drawing did not answer, because the connection to the browser was lost, so nothing was selected.",
            cut.Find("#drawio-junction-list-goto").TextContent));
        Assert.StartsWith("The junction ", cut.Find("#drawio-junction-list-goto").TextContent);
        Assert.Equal("JSDisconnectedException: il circuito non c'e' piu' (prova)", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-junction-list-goto"));
    }

    /// <summary>A junction whose arrow is not on the drawing is said to be missing there, not silently "selected".</summary>
    [Fact]
    public async Task AJunctionWithNoArrowOnTheDrawingIsSaidSo()
    {
        var cut = RenderPage(selectCellAnswer: "no-cell");
        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());

        Act(cut, () => cut.Find($"#drawio-junction-list-table tr[data-junction-id='{AtoB}']").Click());

        cut.WaitForAssertion(() => Assert.Contains("has no arrow in the drawing", cut.Find("#drawio-junction-list-goto").TextContent));
    }

    /// <summary>
    /// The list renders again only when what it shows from its parameters changed (fix round 1): a
    /// render of the page for something else - a device selected on the drawing, which is no arrow -
    /// leaves it alone; the arrow selected there renders it, and marks its row.
    /// </summary>
    [Fact]
    public async Task APageRenderThatChangesNothingTheListShowsDoesNotRenderIt()
    {
        var cut = RenderPage();
        var list = cut.FindComponent<DrawioJunctionList>();
        var renders = list.RenderCount;
        var pageRenders = cut.RenderCount;

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([A.ToString()]));
        cut.WaitForAssertion(() => Assert.True(cut.RenderCount > pageRenders, "The page did not render for the selection."));

        Assert.Equal(renders, list.RenderCount);

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([AtoB.ToString()]));
        cut.WaitForAssertion(() => Assert.Equal("true",
            cut.Find($"#drawio-junction-list-table tr[data-junction-id='{AtoB}']").GetAttribute("aria-selected")));
        Assert.True(list.RenderCount > renders);
    }
}
