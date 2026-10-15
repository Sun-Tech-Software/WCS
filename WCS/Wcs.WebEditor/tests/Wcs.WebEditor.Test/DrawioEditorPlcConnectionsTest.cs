using System.Net;
using AngleSharp.Dom;
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
/// Compito 4, row 62: the grid of the PLC connections (WinForms <c>FrmPCM_PLCS_LIST</c>), through the
/// WHOLE page - the save goes through the page's write gate with its own writer, the edit permit
/// asked for at the moment, and the page's engine rule, and those exist only where the section is
/// wired to the page.
/// </summary>
/// <remarks>
/// Since Task 4b the host's <c>PUT /plc/connections</c> requires the edit permit and an engine in
/// Stop (409 otherwise), and still has no Id rule. The fake refuses a non-Stop engine as the host does
/// (<see cref="DrawioWriteHandler.PlcConnections"/>); the page refuses it first on its own, so the
/// host's refusal is seen only when the engine starts between the two
/// (<see cref="AnEngineStartedFromOutsideBeforeThePutIsARefusalAndTheChangesStay"/>). Every other
/// refusal these tests see is the page's own.
/// </remarks>
public sealed class DrawioEditorPlcConnectionsTest : PlantCanvasBunitContext
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");

    private static readonly PlcConnectionDto Line1 = new(1, "Line 1", "10.0.0.1", 2011, "10.0.0.2", 2012);
    private static readonly PlcConnectionDto Line2 = new(2, "Line 2", "10.0.0.3", 2021, "10.0.0.4", 2022);

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private readonly DrawioWriteHandler _host = new();

    public DrawioEditorPlcConnectionsTest()
    {
        // Shuttle 1 is on connection 2, Shuttle 2 on connection 1: removing either leaves a device behind.
        _host.Items = [Item(A, "Shuttle 1", 10, connection: 2), Item(B, "Shuttle 2", 300, connection: 1)];
        _host.PlcConnections = [Line1, Line2];
    }

    private static PlantItemDto Item(Guid id, string name, double x, decimal connection) =>
        new(id, name, DeviceTypes.Automation, null, null, null, ConnectionTypes.Connected,
            default, 0, null, connection, 0, 0, 0, default!, x, 10, 100, 100, 0);

    private IRenderedComponent<DrawioEditor> RenderPage()
    {
        var state = new EditorSessionState(new Uri("http://localhost:5580"), _host);
        state.UseKey("test-key");
        Services.AddSingleton(state);
        var cut = Render<DrawioEditor>();
        cut.WaitForAssertion(() => Assert.False(cut.Find("#drawio-mode-toggle").HasAttribute("disabled")));
        return cut;
    }

    /// <summary>Finds and triggers in ONE step on the renderer's dispatcher: see DrawioEditorDeviceListTest.Act.</summary>
    private static void Act(IRenderedComponent<DrawioEditor> cut, Action gesture) =>
        cut.InvokeAsync(gesture).GetAwaiter().GetResult();

    private int PlcReads => _host.Requests.Count(r => r == "GET /api/v1/plc/connections");

    private static void Toggle(IRenderedComponent<DrawioEditor> cut) =>
        Act(cut, () => cut.Find("#drawio-plc-connections-toggle").Click());

    private void OpenTheGrid(IRenderedComponent<DrawioEditor> cut)
    {
        Toggle(cut);
        cut.WaitForAssertion(() => Assert.Equal(Math.Min(_host.PlcConnections.Count, 25), Rows(cut).Count));
    }

    private static IReadOnlyList<IElement> Rows(IRenderedComponent<DrawioEditor> cut) =>
        [.. cut.FindAll("#drawio-plc-table tbody tr")];

    private static string Value(IRenderedComponent<DrawioEditor> cut, int row, string field) =>
        Rows(cut)[row].QuerySelector($"input[data-field='{field}']")!.GetAttribute("value") ?? "";

    private static void Type(IRenderedComponent<DrawioEditor> cut, int row, string field, string text) =>
        Act(cut, () => Rows(cut)[row].QuerySelector($"input[data-field='{field}']")!.Input(text));

    private static void Press(IRenderedComponent<DrawioEditor> cut, string id) =>
        Act(cut, () => cut.Find(id).Click());

    private static void AddARow(IRenderedComponent<DrawioEditor> cut) => Press(cut, "#drawio-plc-add");

    private static void Remove(IRenderedComponent<DrawioEditor> cut, int row) =>
        Act(cut, () => Rows(cut)[row].QuerySelector("button.drawio-plc-remove")!.Click());

    private static string Outcome(IRenderedComponent<DrawioEditor> cut) => cut.Find("#drawio-plc-outcome").TextContent;

    /// <summary>What the grid's Problem column says about one row, as it is rendered now.</summary>
    private static string Problem(IRenderedComponent<DrawioEditor> cut, int row) =>
        Rows(cut)[row].QuerySelector("td[data-field='Problem']")!.TextContent;

    private static void AskAndConfirm(IRenderedComponent<DrawioEditor> cut)
    {
        Press(cut, "#drawio-plc-save");
        Press(cut, "#drawio-plc-confirm");
    }

    // ---- Reading --------------------------------------------------------------------------------

    private void SeedManyConnections() => _host.PlcConnections = Enumerable.Range(1, 52)
        .Select(id => new PlcConnectionDto(id, $"Line {id:000}", $"10.0.0.{id}", 2000 + id,
            $"10.1.0.{id}", 3000 + id)).ToList();

    [Fact]
    public void PagingShowsAllRowsAndDisablesTheBoundaryButtons()
    {
        SeedManyConnections();
        var cut = RenderPage();
        OpenTheGrid(cut);
        Assert.Equal(25, Rows(cut).Count);
        Assert.Equal("1–25 of 52", cut.Find("#drawio-plc-page-summary").TextContent);
        Assert.True(cut.Find("#drawio-plc-first").HasAttribute("disabled"));
        Assert.True(cut.Find("#drawio-plc-previous").HasAttribute("disabled"));
        Press(cut, "#drawio-plc-next");
        Assert.Equal("26", Value(cut, 0, "Id"));
        Assert.Equal("page", cut.Find(".drawio-plc-pager button[aria-label='Page 2']").GetAttribute("aria-current"));
        Press(cut, "#drawio-plc-last");
        Assert.Equal("51–52 of 52", cut.Find("#drawio-plc-page-summary").TextContent);
        Assert.True(cut.Find("#drawio-plc-next").HasAttribute("disabled"));
        Assert.True(cut.Find("#drawio-plc-last").HasAttribute("disabled"));
        Press(cut, "#drawio-plc-previous");
        Assert.Equal("26", Value(cut, 0, "Id"));
        Press(cut, "#drawio-plc-first");
        Assert.Equal("1", Value(cut, 0, "Id"));
    }

    [Theory]
    [InlineData("LINE 052")]
    [InlineData("10.0.0.52")]
    [InlineData("2052")]
    [InlineData("10.1.0.52")]
    [InlineData("3052")]
    [InlineData("52")]
    public void TheFilterMatchesEveryDisplayedFieldIgnoringCase(string filter)
    {
        SeedManyConnections();
        var cut = RenderPage();
        OpenTheGrid(cut);
        Act(cut, () => cut.Find("#drawio-plc-filter").Input(filter));
        Assert.Single(Rows(cut));
        Assert.Equal("52", Value(cut, 0, "Id"));
    }

    [Fact]
    public void FilteringReturnsToTheFirstPageAndHandlesNoMatches()
    {
        SeedManyConnections();
        var cut = RenderPage();
        OpenTheGrid(cut);
        Press(cut, "#drawio-plc-last");
        Act(cut, () => cut.Find("#drawio-plc-filter").Input(" Line 052 "));
        Assert.Equal("52", Value(cut, 0, "Id"));
        Assert.Equal("1 of 1", cut.Find("#drawio-plc-page-summary").TextContent);
        Act(cut, () => cut.Find("#drawio-plc-filter").Input("absent"));
        Assert.Empty(Rows(cut));
        Assert.Equal("No PLC connections", cut.Find("#drawio-plc-page-summary").TextContent);
        Assert.All(cut.FindAll(".drawio-plc-pager button"), button => Assert.True(button.HasAttribute("disabled")));
        Act(cut, () => cut.Find("#drawio-plc-filter").Input(""));
        Assert.Equal("1", Value(cut, 0, "Id"));
    }

    [Fact]
    public void AnEditLeavingTheFilterStillSavesTheCompleteWorkingCopy()
    {
        SeedManyConnections();
        var cut = RenderPage();
        OpenTheGrid(cut);
        Act(cut, () => cut.Find("#drawio-plc-filter").Input("Line 052"));
        Type(cut, 0, "Description", "Renamed connection");
        // Still on screen while it is being edited; the filter typed again no longer matches it.
        Assert.Equal("Renamed connection", Value(cut, 0, "Description"));
        Act(cut, () => cut.Find("#drawio-plc-filter").Input("Line 052 "));
        Assert.Empty(Rows(cut));
        Act(cut, () => cut.Find("#drawio-plc-filter").Input(""));
        Press(cut, "#drawio-plc-last");
        Assert.Equal("Renamed connection", Value(cut, 1, "Description"));
        AskAndConfirm(cut);
        cut.WaitForAssertion(() => Assert.Single(_host.PlcConnectionWrites));
        var saved = Assert.Single(_host.PlcConnectionWrites);
        Assert.Equal(52, saved.Length);
        Assert.Equal("Renamed connection", Assert.Single(saved, connection => connection.Id == 52).Description);
        Assert.Equal(Enumerable.Range(1, 52).Select(id => (long)id), saved.Select(connection => connection.Id));
    }

    /// <summary>
    /// The row under the operator's keys stays on screen, key after key, even when its text stops
    /// matching the filter: matched live, it vanished at the first key that broke the match - the
    /// focus with it, the next keys going nowhere and a half-typed value left in the working copy.
    /// </summary>
    [Fact]
    public void ARowBeingTypedInStaysVisibleWhenItsTextStopsMatchingTheFilter()
    {
        SeedManyConnections();
        var cut = RenderPage();
        OpenTheGrid(cut);
        Act(cut, () => cut.Find("#drawio-plc-filter").Input("Line 052"));
        Assert.Single(Rows(cut));

        foreach (var text in new[] { "Line 05", "Line 0", "Line", "Lin", "Li", "L", "", "P", "Pa", "Pack" })
        {
            Type(cut, 0, "Description", text);
            Assert.Single(Rows(cut));
            Assert.Equal("52", Value(cut, 0, "Id"));
            Assert.Equal(text, Value(cut, 0, "Description"));
        }
        Assert.Equal("1 of 1", cut.Find("#drawio-plc-page-summary").TextContent);
    }

    /// <summary>
    /// "Marked below" is true wherever the rows that cannot be saved are: on another page, the grid
    /// goes to the page of the first of them; hidden by the filter, the filter is cleared first.
    /// </summary>
    [Fact]
    public void ASaveRefusedForARowOutOfSightBringsTheFirstMarkedRowOnScreen()
    {
        SeedManyConnections();
        var cut = RenderPage();
        OpenTheGrid(cut);
        Press(cut, "#drawio-plc-last");
        Type(cut, 1, "Id", "fifty-two");
        Press(cut, "#drawio-plc-first");
        Assert.Equal("1–25 of 52", cut.Find("#drawio-plc-page-summary").TextContent);

        Press(cut, "#drawio-plc-save");
        Assert.Contains("marked below", cut.Find("#drawio-plc-outcome").TextContent);
        Assert.Equal("51–52 of 52", cut.Find("#drawio-plc-page-summary").TextContent);
        Assert.Equal("true", Rows(cut)[1].GetAttribute("aria-invalid"));
        Assert.Equal("fifty-two", Value(cut, 1, "Id"));

        Act(cut, () => cut.Find("#drawio-plc-filter").Input("Line 001"));
        Assert.Equal("1", Assert.Single(Rows(cut)).QuerySelector("input[data-field='Id']")!.GetAttribute("value"));
        Press(cut, "#drawio-plc-save");
        Assert.Equal("", cut.Find("#drawio-plc-filter").GetAttribute("value"));
        Assert.Equal("51–52 of 52", cut.Find("#drawio-plc-page-summary").TextContent);
        Assert.Equal("true", Rows(cut)[1].GetAttribute("aria-invalid"));
    }

    [Fact]
    public void AddShowsTheNewRowAndRemovalClampsWhileOriginalRowsRemainRestorable()
    {
        SeedManyConnections();
        var cut = RenderPage();
        OpenTheGrid(cut);
        Act(cut, () => cut.Find("#drawio-plc-filter").Input("Line 001"));
        AddARow(cut);
        Assert.Equal("", cut.Find("#drawio-plc-filter").GetAttribute("value"));
        Assert.Equal("51–53 of 53", cut.Find("#drawio-plc-page-summary").TextContent);
        Assert.Equal("-1", Value(cut, 2, "Id"));
        Remove(cut, 2);
        Remove(cut, 1);
        Assert.Equal("true", Rows(cut)[1].GetAttribute("data-removed"));
        Act(cut, () => Rows(cut)[1].QuerySelector("button.drawio-plc-restore")!.Click());
        Assert.Equal("false", Rows(cut)[1].GetAttribute("data-removed"));
        Assert.Equal("52 shown · 52 total · 52 active", cut.Find("#drawio-plc-count").TextContent);

        // Added rows truly disappear. Removing the only row on page 4 must return to page 3.
        for (var index = 0; index < 24; index++) AddARow(cut);
        Assert.Equal("76 of 76", cut.Find("#drawio-plc-page-summary").TextContent);
        Remove(cut, 0);
        Assert.Equal("51–75 of 75", cut.Find("#drawio-plc-page-summary").TextContent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResolvingAConflictClampsThePageToTheRemainingWorkingCopy(bool keepMine)
    {
        SeedManyConnections();
        var cut = RenderPage();
        OpenTheGrid(cut);
        Type(cut, 0, "Description", "Local change");
        Press(cut, "#drawio-plc-last");
        _host.PlcConnections = _host.PlcConnections.Take(2).ToList();
        Press(cut, "#drawio-plc-reread");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#drawio-plc-conflict")));
        Press(cut, keepMine ? "#drawio-plc-keep-mine" : "#drawio-plc-take-host");
        Assert.Equal("1–2 of 2", cut.Find("#drawio-plc-page-summary").TextContent);
        Assert.Equal("1", Value(cut, 0, "Id"));
        Assert.Equal(keepMine ? "Local change" : "Line 001", Value(cut, 0, "Description"));
    }

    [Fact]
    public void DiscardRestoresTheWholeListAndClampsAnAddedPage()
    {
        SeedManyConnections();
        var cut = RenderPage();
        OpenTheGrid(cut);
        for (var index = 0; index < 24; index++) AddARow(cut);
        Press(cut, "#drawio-plc-discard");
        Assert.Equal("51–52 of 52", cut.Find("#drawio-plc-page-summary").TextContent);
        Assert.Equal("52", Value(cut, 1, "Id"));
    }

    /// <summary>Shut, nothing is read; opened, ONE read, and every row with the WinForms' six columns.</summary>
    [Fact]
    public void OpeningTheGridReadsTheHostsConnectionsOnce()
    {
        var cut = RenderPage();
        Assert.Equal(0, PlcReads);
        Assert.Empty(cut.FindAll("#drawio-plc-table"));

        OpenTheGrid(cut);

        Assert.Equal(1, PlcReads);
        Assert.Equal(["Id", "Description", "PlcAddress", "PcmPort", "LocalAddress", "LocalPort"],
            cut.FindAll("#drawio-plc-table thead th[data-field]").Select(th => th.GetAttribute("data-field")));
        Assert.Equal("2", Value(cut, 1, "Id"));
        Assert.Equal("Line 2", Value(cut, 1, "Description"));
        Assert.Equal("10.0.0.3", Value(cut, 1, "PlcAddress"));
        Assert.Equal("2021", Value(cut, 1, "PcmPort"));
        Assert.Equal("10.0.0.4", Value(cut, 1, "LocalAddress"));
        Assert.Equal("2022", Value(cut, 1, "LocalPort"));
    }

    // ---- Saving -----------------------------------------------------------------------------------

    /// <summary>
    /// Final review, M3: rendered on its own, without the page's way through its write gate, edit
    /// permit and engine rule, the section does not write straight to the host - it used to, through
    /// a direct write. Nothing reaches the host, the line says why, and the typing stays.
    /// </summary>
    [Fact]
    public async Task WithoutThePagesWayThroughTheGateNothingIsSaved()
    {
        var state = new EditorSessionState(new Uri("http://localhost:5580"), _host);
        state.UseKey("test-key");
        Services.AddSingleton(state);
        var cut = Render<DrawioPlcConnections>(parameters => parameters
            .Add(p => p.Open, true)
            .Add(p => p.Graph, new Wcs.WebEditor.Model.PlantGraph([.. _host.Items], [], [])));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("#drawio-plc-table tbody tr").Count));

        await cut.InvokeAsync(() => cut.FindAll("#drawio-plc-table tbody tr")[1].QuerySelector("input[data-field='Description']")!.Input("Line 2 renamed"));
        await cut.InvokeAsync(() => cut.Find("#drawio-plc-save").Click());
        await cut.InvokeAsync(() => cut.Find("#drawio-plc-confirm").Click());

        cut.WaitForAssertion(() => Assert.Equal(
            "The PLC connections were NOT saved: this section was not given the page's way through its write gate, edit permit and engine rule, and it does not write around them. Your changes are still in the grid.",
            cut.Find("#drawio-plc-outcome").TextContent.Trim()));
        Assert.Empty(_host.PlcConnectionWrites);
        Assert.Equal("Line 2 renamed",
            cut.FindAll("#drawio-plc-table tbody tr")[1].QuerySelector("input[data-field='Description']")!.GetAttribute("value"));
    }


    /// <summary>
    /// The WinForms' save, explicit and in two steps: "Save" only asks, naming what changes; "Yes"
    /// writes the WHOLE list through the gate and the permit, and the grid is then what the host
    /// holds - read again from it, not what was sent.
    /// </summary>
    [Fact]
    public void ASaveAsksFirstThenReplacesTheWholeListAndShowsWhatTheHostHolds()
    {
        var cut = RenderPage();
        OpenTheGrid(cut);

        Type(cut, 1, "Description", "Line 2 renamed");
        AddARow(cut);
        Type(cut, 2, "Id", "5");
        Type(cut, 2, "Description", "Line 5");
        Press(cut, "#drawio-plc-save");

        var question = cut.Find("#drawio-plc-confirmation").TextContent;
        Assert.Contains("1 added (5)", question);
        Assert.Contains("1 modified (2)", question);
        Assert.Contains("0 removed", question);
        Assert.Empty(_host.PlcConnectionWrites);
        var readsBefore = PlcReads;
        // While it asks, the blue button is the Yes, the one that writes - Save, which only asked, is
        // switched off - as in the engine's box (passata grafica, final fix round).
        Assert.Contains("drawio-btn-primary", cut.Find("#drawio-plc-confirm").ClassList);
        Assert.True(cut.Find("#drawio-plc-save").HasAttribute("disabled"));

        Press(cut, "#drawio-plc-confirm");

        cut.WaitForAssertion(() => Assert.Contains("Saved", Outcome(cut)));
        var written = Assert.Single(_host.PlcConnectionWrites);
        Assert.Equal(
        [
            Line1,
            Line2 with { Description = "Line 2 renamed" },
            new PlcConnectionDto(5, "Line 5", "127.0.0.1", 2011, "127.0.0.1", 2012),
        ], written);
        Assert.True(_host.EditLockRequests > 0, "The edit permit was not asked for.");
        // One read to check nobody changed the list underneath, one to show what the host holds.
        Assert.Equal(readsBefore + 2, PlcReads);
        Assert.Contains("1 added (5)", Outcome(cut));
        Assert.Empty(cut.FindAll("#drawio-plc-confirmation"));
        Assert.True(cut.Find("#drawio-plc-save").HasAttribute("disabled"));
    }

    /// <summary>"Cancel" on the question writes nothing and keeps what was typed.</summary>
    [Fact]
    public void CancellingTheQuestionWritesNothingAndKeepsTheChanges()
    {
        var cut = RenderPage();
        OpenTheGrid(cut);
        Type(cut, 0, "PlcAddress", "10.9.9.9");

        Press(cut, "#drawio-plc-save");
        Press(cut, "#drawio-plc-cancel");

        Assert.Empty(cut.FindAll("#drawio-plc-confirmation"));
        Assert.Empty(_host.PlcConnectionWrites);
        Assert.Equal("10.9.9.9", Value(cut, 0, "PlcAddress"));
    }

    // ---- The WinForms checks ----------------------------------------------------------------------

    /// <summary>
    /// A new row is born with Id -1, as in the WinForms (PLCCollectionBindingSource_AddingNew): it is
    /// marked, and "Save" asks nothing and writes nothing until it has a real Id.
    /// </summary>
    [Fact]
    public void ARowWithAnIdBelowOneIsMarkedAndNothingIsAskedOrWritten()
    {
        var cut = RenderPage();
        OpenTheGrid(cut);

        AddARow(cut);
        Assert.Equal("-1", Value(cut, 2, "Id"));
        Press(cut, "#drawio-plc-save");

        Assert.Empty(cut.FindAll("#drawio-plc-confirmation"));
        Assert.Contains("Nothing was saved", Outcome(cut));
        Assert.Equal("true", Rows(cut)[2].GetAttribute("aria-invalid"));
        Assert.Contains("greater than zero", Rows(cut)[2].QuerySelector("td[data-field='Problem']")!.TextContent);
        Assert.Equal("false", Rows(cut)[0].GetAttribute("aria-invalid"));
        Assert.Empty(_host.PlcConnectionWrites);
    }

    /// <summary>An Id on two rows marks BOTH rows, and nothing is written.</summary>
    [Fact]
    public void ARepeatedIdMarksBothRowsAndNothingIsWritten()
    {
        var cut = RenderPage();
        OpenTheGrid(cut);

        Type(cut, 1, "Id", "1");
        Press(cut, "#drawio-plc-save");

        Assert.Empty(cut.FindAll("#drawio-plc-confirmation"));
        Assert.All(Rows(cut), row =>
        {
            Assert.Equal("true", row.GetAttribute("aria-invalid"));
            Assert.Contains("Id 1 is on another row too", row.QuerySelector("td[data-field='Problem']")!.TextContent);
        });
        Assert.Empty(_host.PlcConnectionWrites);
    }

    /// <summary>A port that is not a whole number is marked too: the host takes an integer or nothing.</summary>
    [Fact]
    public void APortThatIsNotAWholeNumberIsMarked()
    {
        var cut = RenderPage();
        OpenTheGrid(cut);

        Type(cut, 0, "PcmPort", "20x1");
        Press(cut, "#drawio-plc-save");

        Assert.Empty(cut.FindAll("#drawio-plc-confirmation"));
        Assert.Contains("PCM port", Rows(cut)[0].QuerySelector("td[data-field='Problem']")!.TextContent);
        Assert.Empty(_host.PlcConnectionWrites);
    }

    // ---- A connection that devices still use ------------------------------------------------------

    /// <summary>
    /// Owner's decision, stricter than the WinForms: a save that removes a connection a device uses is
    /// refused before the question - the removed row is marked, the line names the devices and why -
    /// and nothing is written.
    /// </summary>
    [Fact]
    public void RemovingAConnectionADeviceUsesIsRefusedWithTheDevicesNamed()
    {
        var cut = RenderPage();
        OpenTheGrid(cut);

        Remove(cut, 1);
        Assert.Equal("true", Rows(cut)[1].GetAttribute("data-removed"));
        Press(cut, "#drawio-plc-save");

        Assert.Empty(cut.FindAll("#drawio-plc-confirmation"));
        Assert.Contains("connection 2 is used by Shuttle 1", Outcome(cut));
        Assert.Contains("until the plant is loaded again", Outcome(cut));
        Assert.DoesNotContain("Shuttle 2", Outcome(cut));
        Assert.Equal("true", Rows(cut)[1].GetAttribute("aria-invalid"));
        Assert.Contains("Used by Shuttle 1", Rows(cut)[1].QuerySelector("td[data-field='Problem']")!.TextContent);
        Assert.Empty(_host.PlcConnectionWrites);
    }

    /// <summary>A connection no device uses is removed as any change is saved.</summary>
    [Fact]
    public void RemovingAConnectionNoDeviceUsesGoesThrough()
    {
        var line3 = new PlcConnectionDto(3, "Line 3", "10.0.0.5", 2031, "10.0.0.6", 2032);
        _host.PlcConnections = [Line1, Line2, line3];
        var cut = RenderPage();
        OpenTheGrid(cut);

        Remove(cut, 2);
        Press(cut, "#drawio-plc-save");
        Assert.Contains("1 removed (3)", cut.Find("#drawio-plc-confirmation").TextContent);
        Press(cut, "#drawio-plc-confirm");

        cut.WaitForAssertion(() => Assert.Equal([Line1, Line2], Assert.Single(_host.PlcConnectionWrites)));
    }

    /// <summary>
    /// Once the device is moved off the connection - here from the device list, which rereads it into
    /// the page's plant - the same save is allowed.
    /// </summary>
    [Fact]
    public void ADeviceMovedOffTheConnectionMakesTheSameSaveAllowed()
    {
        var cut = RenderPage();
        OpenTheGrid(cut);
        Remove(cut, 1);
        Press(cut, "#drawio-plc-save");
        Assert.Contains("connection 2 is used by Shuttle 1", Outcome(cut));
        Assert.Contains("Used by Shuttle 1", Problem(cut, 1));

        MoveOntoConnection(cut, A, 1);
        // The grid's window stayed open while the device list's was used (spec 2026-09-29: several
        // windows open together), with the same rows and the same changes - and its marks follow the
        // plant the move changed, with no reopening: the row is no longer "used by" anybody.
        Assert.Equal(_host.PlcConnections.Count, Rows(cut).Count);
        cut.WaitForAssertion(() => Assert.Equal("false", Rows(cut)[1].GetAttribute("aria-invalid")), Bound);
        Assert.DoesNotContain("Used by", Problem(cut, 1));
        Assert.Contains("Removed:", Problem(cut, 1));
        Press(cut, "#drawio-plc-save");

        Assert.Contains("1 removed (2)", cut.Find("#drawio-plc-confirmation").TextContent);
        Press(cut, "#drawio-plc-confirm");
        cut.WaitForAssertion(() => Assert.Equal([Line1], Assert.Single(_host.PlcConnectionWrites)));
    }

    /// <summary>
    /// The plant as the page knows it at WRITE time counts: a device moved onto the removed connection
    /// after the question refuses the save inside the gate, and nothing is written.
    /// </summary>
    [Fact]
    public void ADeviceMovedOntoTheRemovedConnectionAfterTheQuestionRefusesTheSave()
    {
        var line3 = new PlcConnectionDto(3, "Line 3", "10.0.0.5", 2031, "10.0.0.6", 2032);
        _host.PlcConnections = [Line1, Line2, line3];
        var cut = RenderPage();
        OpenTheGrid(cut);
        Remove(cut, 2);
        Press(cut, "#drawio-plc-save");
        Assert.NotEmpty(cut.FindAll("#drawio-plc-confirmation"));
        Assert.Equal("false", Rows(cut)[2].GetAttribute("aria-invalid"));

        MoveOntoConnection(cut, A, 3);
        // The grid's window stayed open while the device list's was used (spec 2026-09-29: several
        // windows open together), with the same rows and the same changes - and its marks follow the
        // plant the move changed, with no reopening: the removed row is now "used by" the device.
        Assert.Equal(_host.PlcConnections.Count, Rows(cut).Count);
        cut.WaitForAssertion(() => Assert.Equal("true", Rows(cut)[2].GetAttribute("aria-invalid")), Bound);
        Assert.Contains("Used by Shuttle 1", Problem(cut, 2));
        Press(cut, "#drawio-plc-confirm");

        cut.WaitForAssertion(() => Assert.Contains("the plant changed since the question", Outcome(cut)));
        Assert.Contains("connection 3 is used by Shuttle 1", Outcome(cut));
        Assert.Empty(_host.PlcConnectionWrites);
        Assert.Equal("true", Rows(cut)[2].GetAttribute("data-removed"));
    }

    /// <summary>
    /// Task 4c: the HOST refuses too, against the plant as it holds it. A device moved onto the
    /// connection by another client - here straight on the fake host, so this page does not know - lets
    /// the page's own check through, and the host answers 409 naming the connection and the device. A
    /// definite refusal, not "the engine is not stopped" and not "not known"; the row the host names is
    /// marked with its device; nothing was written, and the changes stay. The connection and its device
    /// come from the problem document's field and are in the sentence; the host's own words are behind
    /// the line's "?" with the rest of its answer (row 37 of the queue, extended to the whole page on
    /// 10/10).
    /// </summary>
    [Fact]
    public void AConnectionTheHostSeesInUseIsRefusedInItsWordsAndItsRowIsMarked()
    {
        var line3 = new PlcConnectionDto(3, "Line 3", "10.0.0.5", 2031, "10.0.0.6", 2032);
        _host.PlcConnections = [Line1, Line2, line3];
        var cut = RenderPage();
        OpenTheGrid(cut);
        _host.Items[1] = _host.Items[1] with { DeviceConnectionId = 3 };

        Remove(cut, 2);
        Press(cut, "#drawio-plc-save");
        Assert.NotEmpty(cut.FindAll("#drawio-plc-confirmation"));
        Press(cut, "#drawio-plc-confirm");

        cut.WaitForAssertion(() => Assert.Contains("NOT saved", Outcome(cut)));
        Assert.Contains("connection 3 is still used by Shuttle 2 on the host (marked below) - move those devices to another connection first", Outcome(cut));
        Assert.DoesNotContain("Connessione PLC", Outcome(cut));
        var answer = DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-plc-outcome");
        Assert.StartsWith("the host answered 409: {", answer);
        Assert.Contains("\"title\":\"Connessione PLC ancora usata da dispositivi\"", answer);
        Assert.Contains("Nothing was written on the host; your changes are still in the grid", Outcome(cut));
        Assert.DoesNotContain("the engine is not stopped", Outcome(cut));
        Assert.DoesNotContain("NOT known", Outcome(cut));
        Assert.Empty(_host.PlcConnectionWrites);
        Assert.Equal("true", Rows(cut)[2].GetAttribute("data-removed"));
        Assert.Equal("true", Rows(cut)[2].GetAttribute("aria-invalid"));
        Assert.Contains("The host says it is used by Shuttle 2", Rows(cut)[2].QuerySelector("td[data-field='Problem']")!.TextContent);
        Assert.Equal("false", Rows(cut)[0].GetAttribute("aria-invalid"));
    }

    /// <summary>
    /// Zero is "no connection" (Task 4c, the host's rule): a device on zero does not use a connection
    /// with Id 0 - which the host's list can hold - so removing that connection is asked and saved.
    /// </summary>
    [Fact]
    public void ADeviceWithNoConnectionDoesNotKeepConnectionZeroFromBeingRemoved()
    {
        var line0 = new PlcConnectionDto(0, "Id zero", "10.0.0.9", 2091, "10.0.0.10", 2092);
        _host.PlcConnections = [line0, Line1, Line2];
        _host.Items = [Item(A, "Shuttle 1", 10, connection: 2), Item(B, "Shuttle 2", 300, connection: 0)];
        var cut = RenderPage();
        OpenTheGrid(cut);

        Remove(cut, 0);
        Press(cut, "#drawio-plc-save");
        Assert.Contains("1 removed (0)", cut.FindAll("#drawio-plc-confirmation").Single().TextContent);
        Press(cut, "#drawio-plc-confirm");

        cut.WaitForAssertion(() => Assert.Equal([Line1, Line2], Assert.Single(_host.PlcConnectionWrites)));
    }

    /// <summary>A device's PLC connection written from the device list (row 58) - the page's own way of moving it.</summary>
    private void MoveOntoConnection(IRenderedComponent<DrawioEditor> cut, Guid itemId, int connection)
    {
        Act(cut, () => cut.Find("#drawio-device-list-toggle").Click());
        var cell = $"#drawio-device-list-table tr[data-item-id='{itemId}'] td[data-property='DeviceConnectionId']";
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(cell)));
        Act(cut, () => cut.Find(cell).DoubleClick());
        Act(cut, () => cut.Find("#drawio-device-list-editor-value").Input(connection.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Act(cut, () => cut.Find("#drawio-device-list-editor-write").Click());
        cut.WaitForAssertion(() => Assert.Contains("Written:", cut.Find("#drawio-device-list-outcome").TextContent));
        Assert.Equal(connection, _host.Items.Single(item => item.ItemId == itemId).DeviceConnectionId);
    }

    // ---- Refusals and unknown outcomes: what was typed stays --------------------------------------

    /// <summary>
    /// The engine running: the page refuses before anything is sent - the host would not (its PUT
    /// asks nothing of the engine) - and says why. What was typed stays.
    /// </summary>
    [Fact]
    public void WithTheEngineRunningNothingIsWrittenAndTheChangesStay()
    {
        _host.NavStatus = NavigatorStatusTypes.Run;
        var cut = RenderPage();
        OpenTheGrid(cut);
        Type(cut, 0, "Description", "typed");

        AskAndConfirm(cut);

        cut.WaitForAssertion(() => Assert.Contains("the engine is running", Outcome(cut)));
        Assert.Contains("NOT saved", Outcome(cut));
        Assert.Contains("only with the engine stopped", Outcome(cut));
        Assert.Empty(_host.PlcConnectionWrites);
        Assert.Equal("typed", Value(cut, 0, "Description"));
    }

    /// <summary>
    /// Paused is refused too: the page's own rule refuses only Run, but paused the engine still holds
    /// the connections open, and resuming does not reopen them (ExecutableCommands.StartNavigator).
    /// </summary>
    [Fact]
    public void WithTheEnginePausedNothingIsWrittenAndTheChangesStay()
    {
        _host.NavStatus = NavigatorStatusTypes.Pause;
        var cut = RenderPage();
        OpenTheGrid(cut);
        Type(cut, 0, "Description", "typed");

        AskAndConfirm(cut);

        cut.WaitForAssertion(() => Assert.Contains("the engine is paused", Outcome(cut)));
        Assert.Contains("NOT saved", Outcome(cut));
        Assert.Contains("resuming does not reopen them", Outcome(cut));
        Assert.DoesNotContain("like every change to the plant", Outcome(cut));
        Assert.Empty(_host.PlcConnectionWrites);
        Assert.Equal("typed", Value(cut, 0, "Description"));
    }

    /// <summary>
    /// Task 4b: the engine started from outside AFTER the page found it stopped and BEFORE its PUT -
    /// here while the check read is held - so the page's own rule let the save through and the host
    /// refuses it (409, <c>PlantQueries.RequireStopped</c>). A definite refusal, in the page's words
    /// and the host's: nothing was written, not "not known", and the changes stay.
    /// </summary>
    [Fact]
    public async Task AnEngineStartedFromOutsideBeforeThePutIsARefusalAndTheChangesStay()
    {
        var cut = RenderPage();
        OpenTheGrid(cut);
        Type(cut, 0, "Description", "typed");
        _host.PlcConnectionsGetGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        AskAndConfirm(cut);
        await _host.PlcConnectionsGetGateReached.Task.WaitAsync(Bound);
        _host.NavStatus = NavigatorStatusTypes.Run;
        _host.PlcConnectionsGetGate.SetResult();

        cut.WaitForAssertion(() => Assert.Contains("NOT saved", Outcome(cut)));
        Assert.Equal("The PLC connections were NOT saved: the host refused them - the engine is not stopped - the PLC connections are saved only with the engine stopped. " +
                     "Nothing was written on the host; your changes are still in the grid.", Outcome(cut));
        // The host's own words behind the line's "?" (row 37 of the queue, extended on 10/10).
        var answer = DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-plc-outcome");
        Assert.StartsWith("the host answered 409: {", answer);
        Assert.Contains("connessioni PLC: il motore \\u00E8 in Run", answer);
        // Task 4b's minor, fixed in Task 4c: the request DID reach the host, which wrote nothing.
        Assert.Contains("Nothing was written on the host; your changes are still in the grid", Outcome(cut));
        Assert.DoesNotContain("Nothing reached the host", Outcome(cut));
        Assert.DoesNotContain("NOT known", Outcome(cut));
        Assert.DoesNotContain("the engine is in Run - stop it before changing the plant", Outcome(cut));
        Assert.Empty(_host.PlcConnectionWrites);
        Assert.Equal("typed", Value(cut, 0, "Description"));
        Assert.False(cut.Find("#drawio-plc-save").HasAttribute("disabled"));
    }

    /// <summary>The permit's request lost on the way: nothing is written, the changes stay.</summary>
    [Fact]
    public void APermitRequestLostOnTheWayWritesNothing()
    {
        var cut = RenderPage();
        OpenTheGrid(cut);
        Type(cut, 0, "Description", "typed");
        _host.HttpFailureRoute = "edit-lock";

        AskAndConfirm(cut);

        // Row 37 of the queue (the owner's answer of 9/10): the permit's sentence with its why, and
        // what came back behind the line's "?" - as in the properties panel, the same gate.
        cut.WaitForAssertion(() => Assert.Equal(
            "The PLC connections were NOT saved. The edit permit could not be asked for because the connection to the host failed: nothing was written. Your changes are still in the grid.",
            Outcome(cut).Trim()));
        Assert.StartsWith("HttpRequestException: ", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-plc-outcome"));
        Assert.Empty(_host.PlcConnectionWrites);
        Assert.Equal("typed", Value(cut, 0, "Description"));
    }

    /// <summary>The permit's answer unreadable (a JsonException the page's permit read lets through): a refusal, nothing written.</summary>
    [Fact]
    public void AnUnreadablePermitAnswerWritesNothing()
    {
        var cut = RenderPage();
        OpenTheGrid(cut);
        Type(cut, 0, "Description", "typed");
        _host.GarbledRoute = "edit-lock";

        AskAndConfirm(cut);

        cut.WaitForAssertion(() => Assert.Equal(
            "The edit permit could not be asked for because the host's answer could not be read: nothing was written. Your changes are still in the grid.",
            Outcome(cut).Trim()));
        Assert.StartsWith("JsonException: ", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-plc-outcome"));
        Assert.Empty(_host.PlcConnectionWrites);
        Assert.Equal("typed", Value(cut, 0, "Description"));
    }

    /// <summary>The check read just before writing fails: nothing was written, and the page cannot tell whether somebody else saved.</summary>
    [Fact]
    public void TheCheckReadFailingBeforeTheWriteWritesNothing()
    {
        var cut = RenderPage();
        OpenTheGrid(cut);
        Type(cut, 0, "Description", "typed");
        _host.FailRoute = "plc-get";

        AskAndConfirm(cut);

        cut.WaitForAssertion(() => Assert.Contains("could not be read just before writing", Outcome(cut)));
        Assert.Equal("Nothing was saved: the host's list could not be read just before writing because the host answered with an error, and without it this page cannot tell whether somebody else saved it meanwhile. " +
                     "Your changes are still in the grid - save again.", Outcome(cut));
        Assert.Equal("the host answered 500 with an empty body", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-plc-outcome"));
        Assert.Empty(_host.PlcConnectionWrites);
        Assert.Equal("typed", Value(cut, 0, "Description"));
    }

    /// <summary>
    /// The list the host fails to give, at the opening and when it is read again: why in the page's
    /// words, and the host's answer behind the line's "?" (row 37 of the queue, extended to the whole
    /// page on 10/10).
    /// </summary>
    [Fact]
    public void AReadTheHostFailsIsOneSentenceWithTheAnswerBehindTheQuestionMark()
    {
        var cut = RenderPage();
        _host.FailRoute = "plc-get";

        Toggle(cut);

        cut.WaitForAssertion(() => Assert.Equal("The PLC connections could not be read from the host because the host answered with an error.",
            cut.Find("#drawio-plc-read-error").TextContent));
        Assert.Equal("the host answered 500 with an empty body", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-plc-read-error"));

        Press(cut, "#drawio-plc-reread");
        cut.WaitForAssertion(() => Assert.Equal(_host.PlcConnections.Count, Rows(cut).Count));
        _host.FailRoute = "plc-get";
        Press(cut, "#drawio-plc-reread");

        cut.WaitForAssertion(() => Assert.Equal(
            "The PLC connections could not be read again from the host because the host answered with an error: the grid keeps what it had - press \"Read again\" before saving.",
            cut.Find("#drawio-plc-read-error").TextContent));
        Assert.Equal("the host answered 500 with an empty body", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-plc-read-error"));
    }

    /// <summary>
    /// A failure no filter names: the outcome is NOT known, the save is over (Save usable again), the
    /// changes stay, and the grid is read again.
    /// </summary>
    [Fact]
    public void AnUnforeseenFailureSaysTheOutcomeIsNotKnownAndReadsAgain()
    {
        var cut = RenderPage();
        OpenTheGrid(cut);
        Type(cut, 0, "Description", "typed");
        _host.PlcConnectionsPutFailure = new InvalidOperationException("something nobody expected");
        var readsBefore = PlcReads;

        AskAndConfirm(cut);

        cut.WaitForAssertion(() => Assert.Equal(
            "It is NOT known whether the PLC connections were saved: an unforeseen failure stopped it. " +
            "Your changes are still in the grid; it is read again from the host - check what the host holds before saving again.", Outcome(cut)));
        Assert.Equal("InvalidOperationException: something nobody expected", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-plc-outcome"));
        // The check read, then the read again after the failure.
        cut.WaitForAssertion(() => Assert.Equal(readsBefore + 2, PlcReads));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#drawio-plc-reading")));
        Assert.Empty(cut.FindAll("#drawio-plc-saving"));
        Assert.False(cut.Find("#drawio-plc-save").HasAttribute("disabled"));
        Assert.Equal("typed", Value(cut, 0, "Description"));
    }

    /// <summary>
    /// A failure no filter names BEFORE the write (here the check read): nothing was written, and the
    /// line says so - "NOT known" belongs to the write stage only.
    /// </summary>
    [Fact]
    public void AnUnforeseenFailureBeforeTheWriteSaysNothingWasSaved()
    {
        var cut = RenderPage();
        OpenTheGrid(cut);
        Type(cut, 0, "Description", "typed");
        _host.PlcConnectionsGetFailure = new InvalidOperationException("something nobody expected");

        AskAndConfirm(cut);

        cut.WaitForAssertion(() => Assert.Equal(
            "Nothing was saved: an unforeseen failure stopped it before anything was written. Your changes are still in the grid - save again.", Outcome(cut)));
        Assert.Equal("InvalidOperationException: something nobody expected", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-plc-outcome"));
        Assert.DoesNotContain("NOT known", Outcome(cut));
        Assert.Empty(_host.PlcConnectionWrites);
        Assert.False(cut.Find("#drawio-plc-save").HasAttribute("disabled"));
        Assert.Equal("typed", Value(cut, 0, "Description"));
    }

    /// <summary>
    /// The permit's request lost on the way while the page last read the engine as paused: the reason
    /// is the permit, not a pause read before - that status is not fresh and is not used.
    /// </summary>
    [Fact]
    public void APermitRequestLostIsNotReportedAsAStaleEngineState()
    {
        _host.NavStatus = NavigatorStatusTypes.Pause;
        var cut = RenderPage();
        OpenTheGrid(cut);
        Type(cut, 0, "Description", "typed");
        _host.HttpFailureRoute = "edit-lock";

        AskAndConfirm(cut);

        cut.WaitForAssertion(() => Assert.Contains("The edit permit could not be asked for", Outcome(cut)));
        Assert.DoesNotContain("paused", Outcome(cut));
        Assert.Empty(_host.PlcConnectionWrites);
    }

    /// <summary>
    /// A read that began before a save and lands after it is older than what the save read back: it is
    /// dropped, and the grid keeps showing what the host holds after the save.
    /// </summary>
    [Fact]
    public async Task AReadStartedBeforeASaveAndLandingAfterItIsDropped()
    {
        var cut = RenderPage();
        OpenTheGrid(cut);
        Type(cut, 0, "Description", "typed");
        _host.PlcConnectionsGetGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Press(cut, "#drawio-plc-reread");
        await _host.PlcConnectionsGetGateReached.Task.WaitAsync(Bound);

        AskAndConfirm(cut);
        cut.WaitForAssertion(() => Assert.Contains("Saved to the host", Outcome(cut)));
        // The save is over and the held read has not answered: it no longer says "Reading...".
        Assert.Empty(cut.FindAll("#drawio-plc-reading"));
        var landed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await cut.InvokeAsync(() => cut.FindComponent<DrawioPlcConnections>().Instance.ReadEndedForTesting = () => landed.TrySetResult());
        _host.PlcConnectionsGetGate.SetResult();
        await landed.Task.WaitAsync(Bound);

        // The held read answered with the list from BEFORE the save ("Line 1"); the grid did not go back to it.
        Assert.Empty(cut.FindAll("#drawio-plc-reading"));
        Assert.Equal("typed", Value(cut, 0, "Description"));
        Assert.True(cut.Find("#drawio-plc-save").HasAttribute("disabled"));
    }

    /// <summary>The permit held by somebody else: nothing is written, the changes stay.</summary>
    [Fact]
    public void WithoutTheEditPermitNothingIsWrittenAndTheChangesStay()
    {
        _host.GrantEditLock = false;
        var cut = RenderPage();
        OpenTheGrid(cut);
        Type(cut, 0, "Description", "typed");

        AskAndConfirm(cut);

        cut.WaitForAssertion(() => Assert.Contains("somebody else has been editing", Outcome(cut)));
        Assert.Empty(_host.PlcConnectionWrites);
        Assert.Equal("typed", Value(cut, 0, "Description"));
    }

    /// <summary>
    /// Another writer of the page holding the gate - the connection mode, held at its own permit
    /// request - refuses the save with this writer's own words.
    /// </summary>
    [Fact]
    public async Task AnotherWriterOfThePageRefusesTheSaveWithItsOwnWords()
    {
        var cut = RenderPage();
        OpenTheGrid(cut);
        Type(cut, 0, "Description", "typed");

        _host.EditLockGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Operations keeps its body in the page while shut: its switch is there to press.
        Press(cut, "#drawio-mode-toggle");
        Press(cut, "#drawio-mode-confirm");
        await _host.EditLockGateReached.Task.WaitAsync(Bound);

        AskAndConfirm(cut);

        cut.WaitForAssertion(() => Assert.Contains(
            "The connection mode of the whole plant is being written right now: the PLC connections were NOT saved.", Outcome(cut)));
        Assert.Empty(_host.PlcConnectionWrites);
        _host.EditLockGate.SetResult();
        cut.WaitForAssertion(() => Assert.Single(_host.ConnectionTypeWrites));
        Assert.Equal("typed", Value(cut, 0, "Description"));
    }

    /// <summary>
    /// A refusal with a status from the host: what it means in the sentence, the host's own words behind
    /// its "?" with the rest of the answer (row 37 of the queue, extended to the whole page on 10/10),
    /// and the changes stay.
    /// </summary>
    [Fact]
    public void ARefusalFromTheHostKeepsItsWordsBehindTheQuestionMarkAndTheChangesStay()
    {
        var cut = RenderPage();
        OpenTheGrid(cut);
        Type(cut, 0, "Description", "typed");
        _host.FailRoute = "plc-put";
        _host.FailureStatus = HttpStatusCode.BadRequest;
        _host.FailureBody = """{"title":"Bad Request","detail":"The list is not what the host expected"}""";

        AskAndConfirm(cut);

        cut.WaitForAssertion(() => Assert.Equal(
            "The PLC connections were NOT saved: the host refused them - the list is not valid for the host. Nothing was written on the host; your changes are still in the grid.",
            Outcome(cut)));
        Assert.Equal("the host answered 400: {\"title\":\"Bad Request\",\"detail\":\"The list is not what the host expected\"}",
            DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-plc-outcome"));
        Assert.Equal("typed", Value(cut, 0, "Description"));
    }

    /// <summary>The PUT without an answer: it is NOT known whether it went through, and the changes stay.</summary>
    [Fact]
    public void AnUnknownOutcomeSaysSoAndTheChangesStay()
    {
        var cut = RenderPage();
        OpenTheGrid(cut);
        Type(cut, 0, "Description", "typed");
        _host.TimeoutRoute = "plc-put";

        AskAndConfirm(cut);

        cut.WaitForAssertion(() => Assert.Contains("NOT known", Outcome(cut)));
        Assert.Equal("It is NOT known whether the PLC connections were saved, because the host did not answer in time - the request may have reached the host anyway. " +
                     "Your changes are still in the grid: press \"Read again\" to see what the host holds before saving again.", Outcome(cut));
        Assert.Equal("TaskCanceledException: La richiesta su 'plc-put' e' scaduta senza risposta (prova).",
            DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-plc-outcome"));
        Assert.Equal("typed", Value(cut, 0, "Description"));
        Assert.False(cut.Find("#drawio-plc-save").HasAttribute("disabled"));
    }

    /// <summary>
    /// Written, but the list could not be read back: it says it WAS saved, and that the grid shows
    /// what was sent. The failure is armed only once the PUT is at the host - the check made just
    /// before it must not be the read that fails.
    /// </summary>
    [Fact]
    public async Task WrittenButNotReadBackSaysItWasSaved()
    {
        var cut = RenderPage();
        OpenTheGrid(cut);
        Type(cut, 0, "Description", "typed");
        _host.PlcConnectionsPutGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        AskAndConfirm(cut);
        await _host.PlcConnectionsPutGateReached.Task.WaitAsync(Bound);
        _host.FailRoute = "plc-get";
        _host.PlcConnectionsPutGate.SetResult();

        cut.WaitForAssertion(() => Assert.Contains("could not be read back", Outcome(cut)));
        Assert.StartsWith("Saved", Outcome(cut).Trim());
        Assert.EndsWith(" But the list could not be read back from the host afterwards because the host answered with an error: the grid shows what was sent. Press \"Read again\" before changing anything else.",
            Outcome(cut));
        Assert.Equal("the host answered 500 with an empty body", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-plc-outcome"));
        Assert.Single(_host.PlcConnectionWrites);
        Assert.Equal("typed", Value(cut, 0, "Description"));
    }

    // ---- Discarding, keeping, and the host changed underneath --------------------------------------

    [Fact]
    public void DiscardingPutsBackWhatTheHostHoldsAndWritesNothing()
    {
        var cut = RenderPage();
        OpenTheGrid(cut);
        Type(cut, 0, "Description", "typed");
        AddARow(cut);

        Press(cut, "#drawio-plc-discard");

        Assert.Equal(2, Rows(cut).Count);
        Assert.Equal("Line 1", Value(cut, 0, "Description"));
        Assert.Empty(_host.PlcConnectionWrites);
        Assert.True(cut.Find("#drawio-plc-discard").HasAttribute("disabled"));
    }

    /// <summary>Shut and opened again with changes pending: the grid is read again, and what was typed is still there.</summary>
    [Fact]
    public void ReopeningWithChangesPendingKeepsThem()
    {
        var cut = RenderPage();
        OpenTheGrid(cut);
        Type(cut, 0, "Description", "typed");

        Toggle(cut);
        Assert.Contains("not saved", cut.Find("#drawio-plc-connections-toggle").TextContent);
        Toggle(cut);

        cut.WaitForAssertion(() => Assert.Equal(2, PlcReads));
        cut.WaitForAssertion(() => Assert.Equal("typed", Value(cut, 0, "Description")));
        Assert.Empty(cut.FindAll("#drawio-plc-conflict"));
    }

    /// <summary>
    /// A read that finds the host's list changed under the pending changes does not overwrite them:
    /// it says so, and the operator chooses. "Keep my changes" keeps them against the list the host
    /// holds NOW - the next question counts against that one.
    /// </summary>
    [Fact]
    public void TheHostChangedUnderneathIsSaidAndKeepingMineCountsAgainstTheNewList()
    {
        var cut = RenderPage();
        OpenTheGrid(cut);
        Type(cut, 0, "Description", "typed");
        _host.PlcConnections = [Line1, Line2, new PlcConnectionDto(3, "Line 3", null, null, null, null)];

        Press(cut, "#drawio-plc-reread");

        cut.WaitForAssertion(() => Assert.Contains("changed on the host", cut.Find("#drawio-plc-conflict").TextContent));
        Assert.Equal("typed", Value(cut, 0, "Description"));
        Assert.True(cut.Find("#drawio-plc-save").HasAttribute("disabled"));

        Press(cut, "#drawio-plc-keep-mine");

        Assert.Empty(cut.FindAll("#drawio-plc-conflict"));
        Assert.Equal("typed", Value(cut, 0, "Description"));
        Press(cut, "#drawio-plc-save");
        // Connection 3 is not in the grid: keeping mine against the new list removes it - and says so.
        Assert.Contains("1 removed (3)", cut.Find("#drawio-plc-confirmation").TextContent);
        Assert.Contains("this save REMOVES them: connection 3", cut.Find("#drawio-plc-confirm-added-elsewhere").TextContent);
    }

    /// <summary>
    /// Keeping mine never reverts somebody else's save on what was not touched here: a row changed on
    /// the host and untouched here is saved with the host's NEW values, and nothing names it.
    /// </summary>
    [Fact]
    public void KeepingMineSavesTheHostsNewValuesOnRowsNotTouchedHere()
    {
        var cut = RenderPage();
        OpenTheGrid(cut);
        Type(cut, 0, "Description", "typed");
        _host.PlcConnections = [Line1, Line2 with { PcmPort = 9999, Description = "changed elsewhere" }];

        Press(cut, "#drawio-plc-reread");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#drawio-plc-conflict")));
        Press(cut, "#drawio-plc-keep-mine");

        Assert.Equal("9999", Value(cut, 1, "PcmPort"));
        Press(cut, "#drawio-plc-save");
        Assert.Empty(cut.FindAll("#drawio-plc-confirm-overriding"));
        Press(cut, "#drawio-plc-confirm");
        cut.WaitForAssertion(() => Assert.Equal(
            [Line1 with { Description = "typed" }, Line2 with { PcmPort = 9999, Description = "changed elsewhere" }],
            Assert.Single(_host.PlcConnectionWrites)));
    }

    /// <summary>
    /// A row changed here AND on the host: the cells changed only on the host take the host's values,
    /// the cell changed on both keeps mine - and the question names the connection as overriding.
    /// </summary>
    [Fact]
    public void KeepingMineOnACellChangedOnBothSidesIsNamedInTheQuestion()
    {
        var cut = RenderPage();
        OpenTheGrid(cut);
        Type(cut, 0, "Description", "typed");
        _host.PlcConnections = [Line1 with { Description = "theirs", PlcAddress = "10.9.9.9" }, Line2];

        Press(cut, "#drawio-plc-reread");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#drawio-plc-conflict")));
        Press(cut, "#drawio-plc-keep-mine");
        Press(cut, "#drawio-plc-save");

        Assert.Contains("replace what was changed on the host after this grid read it: connection 1",
            cut.Find("#drawio-plc-confirm-overriding").TextContent);
        Press(cut, "#drawio-plc-confirm");
        cut.WaitForAssertion(() => Assert.Equal(
            [Line1 with { Description = "typed", PlcAddress = "10.9.9.9" }, Line2],
            Assert.Single(_host.PlcConnectionWrites)));
    }

    /// <summary>
    /// A connection added here with an Id that somebody else also added on the host meanwhile: after
    /// "Keep my changes" it would count as merely "modified", and the save would silently replace the
    /// other client's connection - the question names it.
    /// </summary>
    [Fact]
    public void KeepingMineWithAnIdAddedOnBothSidesIsNamedInTheQuestion()
    {
        var cut = RenderPage();
        OpenTheGrid(cut);
        AddARow(cut);
        Type(cut, 2, "Id", "5");
        Type(cut, 2, "Description", "mine");
        _host.PlcConnections = [Line1, Line2, new PlcConnectionDto(5, "theirs", "10.5.5.5", 2051, "10.5.5.6", 2052)];

        Press(cut, "#drawio-plc-reread");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#drawio-plc-conflict")));
        Press(cut, "#drawio-plc-keep-mine");
        Press(cut, "#drawio-plc-save");

        Assert.Contains("1 modified (5)", cut.Find("#drawio-plc-confirmation").TextContent);
        Assert.Contains("added on the host by somebody else with the same Id - your values replace theirs: connection 5",
            cut.Find("#drawio-plc-confirm-same-id").TextContent);
    }

    /// <summary>A connection removed here and changed on the host meanwhile: the save still removes it, and the question says so.</summary>
    [Fact]
    public void KeepingMineOnARowRemovedHereAndChangedOnTheHostIsNamed()
    {
        var line3 = new PlcConnectionDto(3, "Line 3", "10.0.0.5", 2031, "10.0.0.6", 2032);
        _host.PlcConnections = [Line1, Line2, line3];
        var cut = RenderPage();
        OpenTheGrid(cut);
        Remove(cut, 2);
        _host.PlcConnections = [Line1, Line2, line3 with { Description = "changed elsewhere" }];

        Press(cut, "#drawio-plc-reread");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#drawio-plc-conflict")));
        Press(cut, "#drawio-plc-keep-mine");
        Press(cut, "#drawio-plc-save");

        Assert.Contains("1 removed (3)", cut.Find("#drawio-plc-confirmation").TextContent);
        Assert.Contains("Removed here, but changed on the host meanwhile: connection 3",
            cut.Find("#drawio-plc-confirm-removed-changed").TextContent);
    }

    /// <summary>
    /// Removed, then "Keep my changes", then restored: the row comes back with the host's NEW values,
    /// not the ones read before - nothing of the other client's edit is sent back as if typed here.
    /// </summary>
    [Fact]
    public void ARowRemovedThenRestoredAfterKeepingMineCarriesTheHostsNewValues()
    {
        var line3 = new PlcConnectionDto(3, "Line 3", "10.0.0.5", 2031, "10.0.0.6", 2032);
        _host.PlcConnections = [Line1, Line2, line3];
        var cut = RenderPage();
        OpenTheGrid(cut);
        Type(cut, 0, "Description", "typed");
        Remove(cut, 2);
        var theirs = line3 with { Description = "changed elsewhere", PcmPort = 9031 };
        _host.PlcConnections = [Line1, Line2, theirs];

        Press(cut, "#drawio-plc-reread");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#drawio-plc-conflict")));
        Press(cut, "#drawio-plc-keep-mine");
        Act(cut, () => Rows(cut)[2].QuerySelector("button.drawio-plc-restore")!.Click());
        Press(cut, "#drawio-plc-save");

        Assert.Empty(cut.FindAll("#drawio-plc-confirm-overriding"));
        Press(cut, "#drawio-plc-confirm");
        cut.WaitForAssertion(() => Assert.Equal(
            [Line1 with { Description = "typed" }, Line2, theirs],
            Assert.Single(_host.PlcConnectionWrites)));
    }

    /// <summary>
    /// The same, with a cell edited before removing that the host changed too: that cell keeps what
    /// was typed, the others take the host's values, and the question names the connection as overriding.
    /// </summary>
    [Fact]
    public void ARowEditedRemovedAndRestoredAfterKeepingMineIsNamedAsOverriding()
    {
        var line3 = new PlcConnectionDto(3, "Line 3", "10.0.0.5", 2031, "10.0.0.6", 2032);
        _host.PlcConnections = [Line1, Line2, line3];
        var cut = RenderPage();
        OpenTheGrid(cut);
        Type(cut, 2, "Description", "mine");
        Remove(cut, 2);
        _host.PlcConnections = [Line1, Line2, line3 with { Description = "theirs", PcmPort = 9031 }];

        Press(cut, "#drawio-plc-reread");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#drawio-plc-conflict")));
        Press(cut, "#drawio-plc-keep-mine");
        Act(cut, () => Rows(cut)[2].QuerySelector("button.drawio-plc-restore")!.Click());
        Press(cut, "#drawio-plc-save");

        Assert.Contains("connection 3", cut.Find("#drawio-plc-confirm-overriding").TextContent);
        Press(cut, "#drawio-plc-confirm");
        cut.WaitForAssertion(() => Assert.Equal(
            [Line1, Line2, line3 with { Description = "mine", PcmPort = 9031 }],
            Assert.Single(_host.PlcConnectionWrites)));
    }

    [Fact]
    public void TakingTheHostsListReplacesTheChanges()
    {
        var cut = RenderPage();
        OpenTheGrid(cut);
        Type(cut, 0, "Description", "typed");
        _host.PlcConnections = [Line1 with { Description = "changed elsewhere" }, Line2];

        Press(cut, "#drawio-plc-reread");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#drawio-plc-conflict")));
        Press(cut, "#drawio-plc-take-host");

        Assert.Empty(cut.FindAll("#drawio-plc-conflict"));
        Assert.Equal("changed elsewhere", Value(cut, 0, "Description"));
        Assert.True(cut.Find("#drawio-plc-save").HasAttribute("disabled"));
    }

    /// <summary>With nothing pending, a read simply shows what the host holds: there is nothing to choose.</summary>
    [Fact]
    public void WithNothingPendingAReadTakesTheHostsList()
    {
        var cut = RenderPage();
        OpenTheGrid(cut);
        _host.PlcConnections = [Line1 with { Description = "changed elsewhere" }, Line2];

        Press(cut, "#drawio-plc-reread");

        cut.WaitForAssertion(() => Assert.Equal("changed elsewhere", Value(cut, 0, "Description")));
        Assert.Empty(cut.FindAll("#drawio-plc-conflict"));
    }

    /// <summary>
    /// The list changed on the host between the question and "Yes": the save checks it just before
    /// writing, writes nothing, and asks the operator to choose - it never overwrites somebody
    /// else's save it has not shown.
    /// </summary>
    [Fact]
    public void AHostListChangedBetweenTheQuestionAndYesIsNotOverwritten()
    {
        var cut = RenderPage();
        OpenTheGrid(cut);
        Type(cut, 0, "Description", "typed");
        Press(cut, "#drawio-plc-save");
        _host.PlcConnections = [Line1, Line2 with { PcmPort = 9999 }];

        Press(cut, "#drawio-plc-confirm");

        cut.WaitForAssertion(() => Assert.Contains("Nothing was saved", Outcome(cut)));
        Assert.NotEmpty(cut.FindAll("#drawio-plc-conflict"));
        Assert.Empty(_host.PlcConnectionWrites);
        Assert.Equal("typed", Value(cut, 0, "Description"));
    }
}
