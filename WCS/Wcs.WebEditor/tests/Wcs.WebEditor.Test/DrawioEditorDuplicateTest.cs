using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Live;
using Wcs.WebEditor.Pages;
using Wcs.WebEditor.Session;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// Row 63, compito 5 fix round 2 (owner's decision): the plant-level "Duplicate device" of the
/// properties panel, and the right-click menu's entry that asks it. The WinForms rule
/// (<c>DiagramManager.DuplicateAutomationDevice</c> + <c>ConfigCopier.Copy</c>): a new device of the
/// same type, the whole configuration copied but the identifier, the ASI set to "XXXX". Here the name
/// is the one a device dropped on the drawing gets, not the source's (DrawioDuplicationCoordinator).
/// </summary>
public sealed class DrawioEditorDuplicateTest : PlantCanvasBunitContext
{
    private const string BridgeModule = "./_content/Wcs.WebEditor/drawio-bridge.js";
    private const string LiveModule = "./_content/Wcs.WebEditor/drawio-live.js";
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");
    private static readonly TimeSpan HangCeiling = TimeSpan.FromSeconds(10);

    private readonly FakePlantEventSource _source = new();
    private readonly DrawioWriteHandler _host = new();
    private readonly BunitJSModuleInterop _bridge;
    private readonly List<(string Xml, string Id)> _drawn = [];
    private readonly TaskCompletionSource<string[]> _revealed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string _drawAnswer = "ok";
    private readonly JSRuntimeInvocationHandler<Wcs.WebEditor.Drawio.DrawioMarkedCell[]?> _markedCells;
    private readonly TaskCompletionSource _markedCellsAsked = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>What the bridge's cellBox answers for the source: by default A drawn exactly where the plant has it (rotation 0: its own box IS its envelope).</summary>
    private DrawioEditor.DrawioCellBoxAnswer _sourceBox = new("ok", 10, 20, 100, 80, 0);

    private static readonly PlantItemConfigDto SourceConfig = new()
    {
        ItemId = A,
        Name = "A",
        DeviceType = 0,
        Asi = "A-001",
        Partition = "P1",
        Subitem = "S1",
        AutoExecuteStep = true,
        BuildDestinationItemId = B,
        RelocateItemId = B,
        LinkedCellId = 5.5m,
        DeviceConnectionId = 3m,
        StorageOffset = 7,
        BelongingGroupId = 4,
        TimeOutPlcMoving = 1234,
    };

    public DrawioEditorDuplicateTest()
    {
        var live = JSInterop.SetupModule(LiveModule);
        live.Setup<string>("render", _ => true).SetResult("drawn");
        _bridge = JSInterop.SetupModule(BridgeModule);
        // What the bridge answers is chosen by the test (_drawAnswer); the call is recorded either way.
        foreach (var answer in new[] { "ok", "failed", "ok-not-selected" })
        {
            _bridge.Setup<string?>("drawDeviceCell", call =>
            {
                if (_drawAnswer != answer) return false;
                _drawn.Add(((string)call.Arguments[0]!, (string)call.Arguments[1]!));
                return true;
            }).SetResult(answer);
        }

        // "lost": the interop gives up without an answer (fix round 4) - the cell may or may not be there.
        _bridge.Setup<string?>("drawDeviceCell", call =>
        {
            if (_drawAnswer != "lost") return false;
            _drawn.Add(((string)call.Arguments[0]!, (string)call.Arguments[1]!));
            return true;
        }).SetException(new Microsoft.JSInterop.JSException("the answer was lost (test)"));

        // markedCells, asked after a lost answer: held until the test says what the drawing holds.
        _markedCells = _bridge.Setup<Wcs.WebEditor.Drawio.DrawioMarkedCell[]?>("markedCells", _ =>
        {
            _markedCellsAsked.TrySetResult();
            return true;
        });
        _bridge.Setup<DrawioEditor.DrawioCellBoxAnswer?>("cellBox", _ => true).SetResult(null);
        _bridge.Setup<string?>("revealInPanel", call =>
        {
            if (call.Arguments[0] is not string[] targets) return false;
            _revealed.TrySetResult(targets);
            return true;
        }).SetResult("drawio-duplicate-confirmation");

        _host.Types = [Automation()];
        _host.Items =
        [
            new PlantItemDto(A, "A", DeviceTypes.Automation, "A-001", null, null, default, default, 0, null, 0, 0, 0, 0, default!, 10, 20, 100, 80, 0),
            new PlantItemDto(B, "B", DeviceTypes.Automation, null, null, null, default, default, 0, null, 0, 0, 0, 0, default!, 300, 20, 100, 80, 0),
        ];
        _host.Configs[A] = SourceConfig;
        _host.Configs[B] = new PlantItemConfigDto { ItemId = B, Name = "B", DeviceType = 0 };
    }

    private static DeviceTypeDto Automation() => new(
        "automation", 0, "Automation",
        [new ShapePartDto("M 4 4 H 96 V 96 H 4 Z", "#334155", Filled: false, Rigid: false)],
        [], new ShapePointDto(50, 50), new ShapePointDto(88, 60));

    private async Task<IRenderedComponent<DrawioEditor>> RenderWithASelectedDevice(Guid selected)
    {
        _bridge.Setup<DrawioEditor.DrawioCellBoxAnswer?>("cellBox", call => call.Arguments[0] is string cell && cell == A.ToString())
            .SetResult(_sourceBox);
        var state = new EditorSessionState(new Uri("http://localhost:5580"), _host) { EventSourceForTesting = () => _source };
        state.UseKey("test-key");
        Services.AddSingleton(state);
        var cut = Render<DrawioEditor>();
        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([selected.ToString()]));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#drawio-duplicate-ask")));
        return cut;
    }

    private static async Task AskAsync(IRenderedComponent<DrawioEditor> cut)
    {
        await cut.Find("#drawio-duplicate-ask").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        cut.WaitForAssertion(() => cut.Find("#drawio-duplicate-confirmation"));
    }

    /// <summary>
    /// What the report says, line by line: since row 37 of the queue a line about a failure carries
    /// what came back behind its "?", and that is not what the line says - see <see cref="DetailOf"/>.
    /// </summary>
    private static string Report(IRenderedComponent<DrawioEditor> cut) =>
        string.Join("\n", cut.FindAll("#drawio-duplicate-report > li").Select(line =>
            line.QuerySelector(".drawio-failure-line > span") is { } said
                ? said.TextContent
                : line.TextContent));

    /// <summary>Row 37 of the queue: what is behind the "?" of the report's line <paramref name="index"/>, checked to be inside the note the "?" opens, and shut.</summary>
    private static string DetailOf(IRenderedComponent<DrawioEditor> cut, int index)
    {
        var detail = cut.Find($"#drawio-duplicate-report-{index}-detail");
        Assert.NotNull(detail.Closest($"#drawio-duplicate-report-{index}-help-body"));
        Assert.True(cut.Find($"#drawio-duplicate-report-{index}-help-body").HasAttribute("hidden"), "What came back is open before its \"?\" is pressed.");
        Assert.Null(cut.Find($"#drawio-duplicate-report-{index}").Closest($"#drawio-duplicate-report-{index}-help-body"));
        return detail.TextContent;
    }

    [Fact]
    public async Task TheButtonAsksFirstNamingTheDeviceAndTheNewNameAndCancelWritesNothing()
    {
        var cut = await RenderWithASelectedDevice(A);

        await AskAsync(cut);

        Assert.StartsWith("Duplicate A as a new device named \"Automation 1\"?", cut.Find("#drawio-duplicate-question").TextContent.Trim());
        Assert.Empty(_host.ItemCreations);

        await cut.Find("#drawio-duplicate-cancel").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#drawio-duplicate-confirmation")));
        Assert.Empty(_host.ItemCreations);
        Assert.Empty(_host.GeometryWrites);
    }

    /// <summary>
    /// The copy, field by field against the source: every one the same but the three this page
    /// resets - the identifier (new), the name (the suggested one) and the ASI ("XXXX", the WinForms
    /// ConfigCopier's). The links to other devices, the warehouse cell and the PLC connection are
    /// copied, as the WinForms copies them.
    /// </summary>
    [Fact]
    public async Task ConfirmingCreatesADeviceWithTheSourcesConfigurationDrawsAndSaysIt()
    {
        var cut = await RenderWithASelectedDevice(A);
        await AskAsync(cut);

        await cut.Find("#drawio-duplicate-confirm").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        cut.WaitForAssertion(() => Assert.Single(_drawn));
        var created = Assert.Single(_host.ItemCreations);
        Assert.NotEqual(A, created.ItemId);
        Assert.NotEqual(Guid.Empty, created.ItemId);
        Assert.Equal("Automation 1", created.Name);
        Assert.Equal("XXXX", created.Asi);
        // Owner's decision (fix round 3): born with no linked warehouse cell - the source has 5.5.
        Assert.Equal(0m, created.LinkedCellId);
        foreach (var property in typeof(PlantItemConfigDto).GetProperties().Where(p => p.CanWrite
                     && p.Name is not (nameof(PlantItemConfigDto.ItemId) or nameof(PlantItemConfigDto.Name) or nameof(PlantItemConfigDto.Asi)
                         or nameof(PlantItemConfigDto.LinkedCellId))))
            Assert.True(Equals(property.GetValue(SourceConfig), property.GetValue(created)),
                $"{property.Name}: the source has {property.GetValue(SourceConfig)}, the duplicate {property.GetValue(created)}.");

        var geometry = Assert.Single(_host.GeometryWrites);
        Assert.Equal(created.ItemId, geometry.ItemId);
        Assert.Equal((110d, 120d, 100d, 80d, 0d), (geometry.X, geometry.Y, geometry.Width, geometry.Height, geometry.Rotation));

        var (xml, id) = Assert.Single(_drawn);
        Assert.Equal(created.ItemId.ToString(), id);
        Assert.Contains($"<object id=\"{created.ItemId}\" label=\"Automation 1\" wcs=\"device\">", xml);

        cut.WaitForAssertion(() =>
        {
            var report = Report(cut);
            Assert.Contains($"Created \"Automation 1\" ({created.ItemId}), a duplicate of A, at (110, 120) - 100 to the right of and 100 below where A is drawn now.", report);
            Assert.Contains("Configuration copied from A: every field but the identifier, the name (\"Automation 1\"), the ASI (\"A-001\" -> \"XXXX\") and the linked warehouse cell (5.5 -> none: link it from the panel if needed)", report);
            Assert.Contains("\"Automation 1\" is drawn on the canvas and selected", report);
            Assert.Empty(cut.FindAll("#drawio-duplicate-confirmation"));
        });
    }

    /// <summary>
    /// Owner's decision of 27/9 on Pause: a device is duplicated only with the engine stopped. The
    /// question says so while the page knows the engine is paused, and holds its answer; the write
    /// checks again (bUnit's Click ignores "disabled") and sends nothing.
    /// </summary>
    [Fact]
    public async Task WithTheEnginePausedTheQuestionSaysWhyAndNothingIsDuplicated()
    {
        var cut = await RenderWithASelectedDevice(A);
        _host.NavStatus = NavigatorStatusTypes.Pause;
        _source.RaiseNavigator(new EngineStatus(NavigatorStatusTypes.Pause, DefragmentationTypes.None, "t", DateTime.UtcNow));
        await AskAsync(cut);

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(
                "Not now: the engine is paused, and a device is duplicated only with the engine stopped: it would be born without its connection - the engine gives devices the simulator or the PLC only when it opens the connections, and resuming from Pause does not open them again.",
                cut.Find("#drawio-duplicate-not-now").TextContent.Trim());
            Assert.True(cut.Find("#drawio-duplicate-confirm").HasAttribute("disabled"));
        });

        await cut.Find("#drawio-duplicate-confirm").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        cut.WaitForAssertion(() => Assert.StartsWith("NOT duplicated: The engine is paused, and a device is duplicated only with the engine stopped", Report(cut).Trim()));
        Assert.Empty(_host.ItemCreations);
        Assert.Empty(_drawn);
    }

    [Fact]
    public async Task WithTheEngineRunningNothingIsDuplicated()
    {
        var cut = await RenderWithASelectedDevice(A);
        await AskAsync(cut);
        _host.NavStatus = NavigatorStatusTypes.Run;

        await cut.Find("#drawio-duplicate-confirm").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        cut.WaitForAssertion(() => Assert.Equal(
            "NOT duplicated: Read only: the engine is running - stop it before changing the plant. Nothing was written.",
            Report(cut).Trim()));
        Assert.Empty(_host.ItemCreations);
        Assert.Empty(_drawn);
    }

    /// <summary>
    /// Row 37 of the queue (the owner's answer of 9/10): the edit permit that could not be asked for is
    /// said in the report's line with its why, and what came back is behind the line's "?" - the
    /// sentence used to end on the failure's message.
    /// </summary>
    [Fact]
    public async Task APermitThatCannotBeAskedForIsSaidWithTheFailureBehindTheQuestionMark()
    {
        var cut = await RenderWithASelectedDevice(A);
        await AskAsync(cut);
        _host.HttpFailureRoute = "edit-lock";

        await cut.Find("#drawio-duplicate-confirm").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        cut.WaitForAssertion(() => Assert.Equal(
            "NOT duplicated: The edit permit could not be asked for because the connection to the host failed: nothing was written.",
            Report(cut).Trim()));
        Assert.StartsWith("HttpRequestException: ", DetailOf(cut, 0));
        Assert.Empty(_host.ItemCreations);
        Assert.Empty(_drawn);
    }

    [Fact]
    public async Task AHostRefusalOfTheCreationIsSaidAndNothingIsDrawn()
    {
        var cut = await RenderWithASelectedDevice(A);
        await AskAsync(cut);
        _host.FailRoute = "item-post";
        _host.FailureStatus = HttpStatusCode.Conflict;
        // Confirmation review, M-E: a 409 that is NOT Run, in the host's own words - behind the line's
        // "?" since row 37 of the queue, with the rest of the host's answer.
        _host.FailureBody = System.Text.Json.JsonSerializer.Serialize(new
        {
            title = "Il motore non è fermo",
            status = 409,
            detail = "creazione del dispositivo: il motore è in Recover",
        });

        await cut.Find("#drawio-duplicate-confirm").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        cut.WaitForAssertion(() => Assert.Equal(
            "NOT duplicated because the host refused it - the plant's state does not allow it now: nothing was created on the plant.",
            Report(cut).Trim()));
        Assert.Equal(
            "the host answered 409: {\"title\":\"Il motore non \\u00E8 fermo\",\"status\":409,\"detail\":\"creazione del dispositivo: il motore \\u00E8 in Recover\"}",
            DetailOf(cut, 0));
        Assert.Empty(_drawn);
    }

    /// <summary>
    /// Row 37 of the queue: the source's configuration cannot be read just before the copy. The line
    /// says why in words; what the client said is behind its "?", where the sentence used to carry it.
    /// </summary>
    [Fact]
    public async Task ASourceThatCannotBeReadIsSaidInWordsWithWhatCameBackBehindTheQuestionMark()
    {
        var cut = await RenderWithASelectedDevice(A);
        await AskAsync(cut);
        _host.HttpFailureRoute = "config-get";

        await cut.Find("#drawio-duplicate-confirm").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        cut.WaitForAssertion(() => Assert.Equal(
            "NOT duplicated: the configuration of A could not be read from the host because the connection to the host failed, so nothing was created.",
            Report(cut).Trim()));
        Assert.Equal("HttpRequestException: Il trasporto su 'config-get' e' fallito (Unknown) (prova).", DetailOf(cut, 0));
        Assert.Empty(_host.ItemCreations);
    }

    /// <summary>
    /// Row 37 of the queue: the creation gets no answer, and the host cannot be asked afterwards
    /// either. The line gives both whys in words, and both answers behind its "?", one per line.
    /// </summary>
    [Fact]
    public async Task TwoFailuresInOneLineAreBothSaidInWordsAndBothBehindTheQuestionMark()
    {
        var cut = await RenderWithASelectedDevice(A);
        await AskAsync(cut);
        _host.TimeoutRoute = "item-post";
        _host.SingleItemFailure = new HttpRequestException("the connection was reset (test)");

        await cut.Find("#drawio-duplicate-confirm").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        cut.WaitForAssertion(() => Assert.StartsWith(
            "It is NOT known whether the duplicate of A was created - the writing failed because the host did not answer in time, " +
            "and the host could not be asked afterwards because the connection to the host failed -, and if it was, it is ",
            Report(cut).Trim()));
        Assert.Equal(
            ["The duplicate: TaskCanceledException: La richiesta su 'item-post' e' scaduta senza risposta (prova).",
             "The question afterwards: HttpRequestException: the connection was reset (test)"],
            DetailOf(cut, 0).Split('\n'));
    }

    /// <summary>
    /// The device is created with the copy, and then its position is refused: it exists on the plant,
    /// not placed and not drawn - said as that, never as a success and never as "nothing happened".
    /// </summary>
    [Fact]
    public async Task APositionThatIsNotWrittenIsSaidAsAPartialDuplicate()
    {
        var cut = await RenderWithASelectedDevice(A);
        await AskAsync(cut);
        _host.FailRoute = "geometry";

        await cut.Find("#drawio-duplicate-confirm").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        cut.WaitForAssertion(() =>
        {
            var report = Report(cut);
            Assert.StartsWith($"PARTLY duplicated: \"Automation 1\" ({_host.ItemCreations.Single().ItemId}) exists on the plant, but its position was NOT written", report.Trim());
            Assert.Contains("NOT written because the host answered with an error, so it is not drawn - it shows among the devices with no shape.", report);
            Assert.Contains("Configuration copied from A", report);
        });
        Assert.Equal("the host answered 500 with an empty body", DetailOf(cut, 0));
        Assert.Empty(_drawn);
    }

    [Fact]
    public async Task ACanvasThatDoesNotTakeTheCellIsSaid()
    {
        _drawAnswer = "failed";
        var cut = await RenderWithASelectedDevice(A);
        await AskAsync(cut);

        await cut.Find("#drawio-duplicate-confirm").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        cut.WaitForAssertion(() => Assert.Contains(
            "is on the plant with its configuration and position, but it could NOT be drawn on the canvas (draw.io refused the cell - the browser console says why). It stays on the plant;",
            Report(cut)));
        Assert.Single(_host.ItemCreations);
    }

    /// <summary>The right-click entry asks the panel's own question and brings it into view: nothing is written until "Duplicate" is pressed there.</summary>
    [Fact]
    public async Task TheMenuEntryAsksThePanelsQuestion()
    {
        var cut = await RenderWithASelectedDevice(A);

        await cut.InvokeAsync(() => cut.Instance.OnDrawioMenuEntry(A.ToString(), "duplicate-device"));

        cut.WaitForAssertion(() => Assert.StartsWith("Duplicate A as a new device named \"Automation 1\"?",
            cut.Find("#drawio-duplicate-question").TextContent.Trim()));
        Assert.Equal(["drawio-duplicate-confirmation"], await _revealed.Task.WaitAsync(HangCeiling));
        Assert.Empty(_host.ItemCreations);

        await cut.Find("#drawio-duplicate-confirm").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        cut.WaitForAssertion(() => Assert.Single(_host.ItemCreations));
    }

    [Fact]
    public async Task AnotherSelectionTakesTheQuestionAway()
    {
        var cut = await RenderWithASelectedDevice(A);
        await AskAsync(cut);

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([B.ToString()]));
        cut.WaitForAssertion(() => Assert.Contains("B, as the host has it", cut.Find("#drawio-property-subject").TextContent));
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([A.ToString()]));

        cut.WaitForAssertion(() =>
        {
            Assert.NotEmpty(cut.FindAll("#drawio-duplicate-ask"));
            Assert.Empty(cut.FindAll("#drawio-duplicate-confirmation"));
        });
    }

    /// <summary>
    /// Fix round 3, IMPORTANT 1: a duplicate the canvas refused must not be deleted by the next save.
    /// The save takes the page's plant as the one it opened, and a drawable device of it with no cell
    /// on the drawing is one the operator deleted (DrawioDelta) - so the device goes back out of the
    /// page's plant, and the save leaves it alone. The drawing handed to the save is the one on the
    /// canvas: A and B, and no cell for the duplicate.
    /// </summary>
    [Fact]
    public async Task ADuplicateTheCanvasRefusedIsNotDeletedByTheNextSave()
    {
        _drawAnswer = "failed";
        var drawing = Wcs.WebEditor.Drawio.DrawioDocument.Build(
            new Wcs.WebEditor.Model.PlantGraph([.. _host.Items], [], [.. _host.Types]), null);
        var cut = await RenderWithASelectedDevice(A);
        await AskAsync(cut);
        await cut.Find("#drawio-duplicate-confirm").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        cut.WaitForAssertion(() => Assert.Contains(
            "It stays on the plant; this page shows it - on the drawing and in the lists - only after a reload, and until then the save leaves it alone.",
            Report(cut)));
        var created = Assert.Single(_host.ItemCreations).ItemId;

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSaved(drawing));

        Assert.DoesNotContain(created, _host.ItemDeletes);
        Assert.Contains(_host.Items, item => item.ItemId == created);
    }

    /// <summary>
    /// Fix round 3, MINOR 1: the name the question offered is the one written - and if another device
    /// took it between the question and the answer (here a shape dropped and created), nothing is
    /// written and the report says why.
    /// </summary>
    [Fact]
    public async Task ANameTakenBetweenTheQuestionAndTheAnswerWritesNothing()
    {
        var cut = await RenderWithASelectedDevice(A);
        await AskAsync(cut);

        await cut.InvokeAsync(() => cut.Instance.OnDrawioCellsAdded(
            [new Wcs.WebEditor.Drawio.DrawioAddedCell("dropped-1", "device", "0", Wcs.WebEditor.Drawio.DrawioDocument.DefaultLayerId, 500, 500, 100, 80)]));
        cut.WaitForAssertion(() => Assert.Equal("Automation 1", Assert.Single(_host.ItemCreations).Name));
        Assert.StartsWith("Duplicate A as a new device named \"Automation 1\"?", cut.Find("#drawio-duplicate-question").TextContent.Trim());

        await cut.Find("#drawio-duplicate-confirm").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        cut.WaitForAssertion(() => Assert.Equal(
            "NOT duplicated: the name \"Automation 1\" the question offered is no longer free - another device has taken it since. Nothing was created: ask again, and the question offers a free name.",
            Report(cut).Trim()));
        Assert.Single(_host.ItemCreations);
        Assert.Empty(_drawn);
    }

    /// <summary>
    /// Fix round 3, MINOR 2: the duplicate goes next to the source as it is DRAWN now - moved and not
    /// saved, here from (10, 20) to (500, 400) - as the WinForms put it next to the live node.
    /// </summary>
    [Fact]
    public async Task TheDuplicateGoesNextToWhereTheSourceIsDrawn()
    {
        _sourceBox = new DrawioEditor.DrawioCellBoxAnswer("ok", 500, 400, 100, 80, 0);
        var cut = await RenderWithASelectedDevice(A);
        await AskAsync(cut);

        await cut.Find("#drawio-duplicate-confirm").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        cut.WaitForAssertion(() => Assert.Single(_drawn));
        var geometry = Assert.Single(_host.GeometryWrites);
        Assert.Equal((600d, 500d, 100d, 80d, 0d), (geometry.X, geometry.Y, geometry.Width, geometry.Height, geometry.Rotation));
        cut.WaitForAssertion(() => Assert.Contains("at (600, 500) - 100 to the right of and 100 below where A is drawn now.", Report(cut)));
    }

    /// <summary>When the drawing cannot say where the source is, the plant's position is used - and the report says so.</summary>
    [Fact]
    public async Task WithoutTheSourcesCellThePlantPositionIsUsedAndSaid()
    {
        _sourceBox = new DrawioEditor.DrawioCellBoxAnswer("no-cell", 0, 0, 0, 0, 0);
        var cut = await RenderWithASelectedDevice(A);
        await AskAsync(cut);

        await cut.Find("#drawio-duplicate-confirm").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        cut.WaitForAssertion(() => Assert.Single(_drawn));
        var geometry = Assert.Single(_host.GeometryWrites);
        Assert.Equal((110d, 120d), (geometry.X, geometry.Y));
        cut.WaitForAssertion(() => Assert.Contains(
            "at (110, 120) - 100 to the right of and 100 below where the plant has A - the drawing could not say where it is drawn (it has no cell on the drawing).",
            Report(cut)));
    }

    /// <summary>
    /// Final review, M8: the drawing could not say where the source is because the call to the page's
    /// script did not come back with an answer - and why is the failure's own. "The page did not hear
    /// draw.io's answer" was false for a script that threw: it answered, with an error.
    /// </summary>
    [Theory]
    [InlineData("threw", "the page's script failed")]
    [InlineData("timeout", "the page's script did not answer in time")]
    [InlineData("disconnected", "the connection to the browser was lost")]
    public async Task ASourceTheScriptCannotPlaceIsSaidWithTheFailuresOwnWhy(string failure, string why)
    {
        var cut = await RenderWithASelectedDevice(A);
        Exception thrown = failure switch
        {
            "threw" => new Microsoft.JSInterop.JSException("cellBox broke (test)"),
            "timeout" => new TaskCanceledException("cellBox ran out of time (test)"),
            _ => new Microsoft.JSInterop.JSDisconnectedException("the circuit is gone (test)"),
        };
        // Planned after RenderWithASelectedDevice's answer for the same cell: the latest plan is the one taken.
        _bridge.Setup<DrawioEditor.DrawioCellBoxAnswer?>("cellBox", call => call.Arguments[0] is string cell && cell == A.ToString())
            .SetException(thrown);
        await AskAsync(cut);

        await cut.Find("#drawio-duplicate-confirm").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        cut.WaitForAssertion(() => Assert.Contains(
            $"at (110, 120) - 100 to the right of and 100 below where the plant has A - the drawing could not say where it is drawn ({why}).",
            Report(cut)));
    }

    private void PlaceA(double x, double y, double width, double height, double rotation) =>
        _host.Items[0] = _host.Items[0] with { X = x, Y = y, Width = width, Height = height, Rotation = rotation };

    /// <summary>A's cell as the drawing holds it for the plant's A: its own box (DrawioDocument's), two decimals.</summary>
    private DrawioEditor.DrawioCellBoxAnswer DrawnAsThePlantHasIt()
    {
        var a = _host.Items[0];
        var (x, y, width, height) = Wcs.WebEditor.Drawing.NodePlacement.OwnBox(a.X!.Value, a.Y!.Value, a.Width!.Value, a.Height!.Value, a.Rotation!.Value);
        return new("ok", Math.Round(x, 2), Math.Round(y, 2), Math.Round(width, 2), Math.Round(height, 2), a.Rotation!.Value);
    }

    private async Task<PlantItemGeometryDto> DuplicateAAndReadTheGeometryAsync()
    {
        var cut = await RenderWithASelectedDevice(A);
        await AskAsync(cut);
        await cut.Find("#drawio-duplicate-confirm").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#drawio-duplicate-report")));
        _lastCut = cut;
        return Assert.Single(_host.GeometryWrites);
    }

    private IRenderedComponent<DrawioEditor>? _lastCut;

    /// <summary>
    /// Fix round 4, IMPORTANT 1: at 45 degrees the drawn box IS the envelope (OwnBox cannot invert
    /// it), and a bare EnvelopeBox of it came out a square root of two larger. Through the save's own
    /// rule an unchanged source is the plant's geometry exactly: 141.42 x 141.42 at 45, moved by 100.
    /// </summary>
    [Fact]
    public async Task ASourceAt45DegreesIsDuplicatedAtItsOwnSize()
    {
        PlaceA(10, 20, 141.42, 141.42, 45);
        _sourceBox = DrawnAsThePlantHasIt();

        var geometry = await DuplicateAAndReadTheGeometryAsync();

        Assert.Equal((110d, 120d, 141.42, 141.42, 45d), (geometry.X, geometry.Y, geometry.Width, geometry.Height, geometry.Rotation));
    }

    /// <summary>
    /// A source turned to 45 degrees on the canvas and not saved: the save would refuse that box, so
    /// the duplicate does not take it either - it goes next to where the plant has the source, and
    /// the report says why.
    /// </summary>
    [Fact]
    public async Task ASourceTurnedTo45OnTheCanvasIsDuplicatedFromThePlantAndItIsSaid()
    {
        _sourceBox = new DrawioEditor.DrawioCellBoxAnswer("ok", 10, 20, 100, 80, 45);

        var geometry = await DuplicateAAndReadTheGeometryAsync();

        Assert.Equal((110d, 120d, 100d, 80d, 0d), (geometry.X, geometry.Y, geometry.Width, geometry.Height, geometry.Rotation));
        _lastCut!.WaitForAssertion(() => Assert.Contains(
            "below where the plant has A - where it is drawn now cannot be written as a plant position (it was turned to 45 degrees and not saved, which the plant cannot represent), and the save would refuse it too.",
            Report(_lastCut!)));
    }

    /// <summary>An unchanged source at another rotation: the plant's size exactly, not the drawing's two decimals run back through EnvelopeBox.</summary>
    [Fact]
    public async Task AnUnchangedSourceAtAnotherRotationKeepsItsExactSize()
    {
        PlaceA(10, 20, 126.64, 119.28, 30);
        _sourceBox = DrawnAsThePlantHasIt();

        var geometry = await DuplicateAAndReadTheGeometryAsync();

        Assert.Equal((110d, 120d, 126.64, 119.28, 30d), (geometry.X, geometry.Y, geometry.Width, geometry.Height, geometry.Rotation));
    }

    /// <summary>Fix round 4, MINOR 1: added, but not selected - it IS drawn, said as such, and it stays in the page's plant (the save does not refuse its cell).</summary>
    [Fact]
    public async Task ADuplicateDrawnButNotSelectedIsSaidAndKept()
    {
        _drawAnswer = "ok-not-selected";
        var cut = await RenderWithASelectedDevice(A);
        await AskAsync(cut);
        await cut.Find("#drawio-duplicate-confirm").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        cut.WaitForAssertion(() => Assert.Contains(
            "\"Automation 1\" is drawn on the canvas, but it was not selected (draw.io could not select it - the browser console says why): click it to see its properties.",
            Report(cut)));
        var created = Assert.Single(_host.ItemCreations).ItemId;

        // The drawing now holds A, B and the duplicate's cell: a save must neither delete it nor refuse it.
        var drawing = Wcs.WebEditor.Drawio.DrawioDocument.Build(new Wcs.WebEditor.Model.PlantGraph([.. _host.Items], [], [.. _host.Types]), null);
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSaved(drawing));

        Assert.DoesNotContain(created, _host.ItemDeletes);
        cut.WaitForAssertion(() => Assert.DoesNotContain(created.ToString(), cut.Find("#drawio-save-report").TextContent));
    }

    [Theory]
    [InlineData(true, "is drawn on the canvas, but it was not selected (the page did not hear draw.io's answer, but the cell is on the drawing)")]
    [InlineData(false, "could NOT be drawn on the canvas (its answer was lost, and the drawing holds no cell for it)")]
    public async Task AnAnswerThatNeverCameIsSettledByAskingTheDrawing(bool cellThere, string said)
    {
        _drawAnswer = "lost";
        var cut = await RenderWithASelectedDevice(A);
        await AskAsync(cut);
        var confirming = cut.Find("#drawio-duplicate-confirm").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        await _markedCellsAsked.Task.WaitAsync(HangCeiling);
        var created = await cut.InvokeAsync(() => Assert.Single(_host.ItemCreations).ItemId);

        await cut.InvokeAsync(() => _markedCells.SetResult(cellThere
            ? [new Wcs.WebEditor.Drawio.DrawioMarkedCell(created.ToString(), "device")]
            : []));
        await confirming;

        cut.WaitForAssertion(() => Assert.Contains(said, Report(cut)));
        // Row 37 of the queue: how the answer was lost is behind the line's "?" - the third, after the
        // creation and the copy.
        Assert.Equal("JSException: the answer was lost (test)", DetailOf(cut, 2));
    }

    /// <summary>The answer lost AND the drawing not answerable: said as not known, and the device is left out of the save rather than deleted by it.</summary>
    [Fact]
    public async Task AnAnswerThatNeverCameAndADrawingThatCannotBeAskedIsNotKnown()
    {
        _drawAnswer = "lost";
        var cut = await RenderWithASelectedDevice(A);
        await AskAsync(cut);
        var confirming = cut.Find("#drawio-duplicate-confirm").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        await _markedCellsAsked.Task.WaitAsync(HangCeiling);

        await cut.InvokeAsync(() => _markedCells.SetException(new Microsoft.JSInterop.JSException("the drawing does not answer (test)")));
        await confirming;

        cut.WaitForAssertion(() => Assert.Contains(
            "but it is NOT known whether it was drawn on the canvas (its answer was lost, and the drawing could not be asked afterwards). The save leaves it alone until a reload shows it;",
            Report(cut)));
        Assert.Equal("JSException: the answer was lost (test)", DetailOf(cut, 2));
    }

    /// <summary>
    /// Fix round 4, MINOR 2: a duplicate the canvas refused is out of the page's plant, but its name is
    /// taken on the host. The next question does not offer it, and the write-time check refuses it.
    /// </summary>
    [Fact]
    public async Task TheNameOfADuplicateNotShownIsNotOfferedOrWrittenAgain()
    {
        _drawAnswer = "failed";
        var cut = await RenderWithASelectedDevice(A);
        await AskAsync(cut);
        await cut.Find("#drawio-duplicate-confirm").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        cut.WaitForAssertion(() => Assert.Contains("could NOT be drawn on the canvas", Report(cut)));

        await AskAsync(cut);
        Assert.StartsWith("Duplicate A as a new device named \"Automation 2\"?", cut.Find("#drawio-duplicate-question").TextContent.Trim());

        // The write-time check, asked directly under the name already taken (the question cannot be made to offer it).
        var duplication = (Wcs.WebEditor.Drawio.Coordination.DrawioDuplicationCoordinator)typeof(DrawioEditor)
            .GetField("_duplication", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(cut.Instance)!;
        await cut.InvokeAsync(() => duplication.DuplicateAsync(A, "Automation 1"));

        cut.WaitForAssertion(() => Assert.StartsWith("NOT duplicated: the name \"Automation 1\" the question offered is no longer free", Report(cut).Trim()));
        Assert.Single(_host.ItemCreations);
    }
}
