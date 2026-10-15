using System.Net;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Pages;
using Wcs.WebEditor.Session;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// The plant's file on <c>/editor/drawio</c> (rows 43 and 44 of the parity table): the very
/// <c>ImportExport</c> component <c>/editor</c> has been docking since task 12, hung on this page
/// with the two things this page needs and that page did not - the edit permit, and the gate that
/// keeps the writing gestures from crossing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is a suite of its own and not more of <c>ImportExportTest</c>.</b> That suite measures
/// the component ALONE and is the net that says the move succeeded: it is not touched by this
/// batch, and it must not be - a test changed to fit the thing it was watching has stopped
/// watching. What is measured here is the WIRING, which only exists on this page: whether the
/// permit is really asked for, what a refusal says, and what happens when an import and one of the
/// other four writers reach for the plant at the same moment.
/// </para>
/// <para>
/// <b>The permit is the reason this wiring exists at all.</b> Measured against a real host on
/// 2026-09-20, with <c>/editor/drawio</c> freshly opened and nothing saved yet: nobody held the
/// permit, and <c>PUT /plant/graph</c> without one came back <b>423</b>. On <c>/editor</c> that
/// could not happen - the page took the permit as it opened - so the component never had to ask,
/// and here it would have met the host's refusal and told the operator his plant "may have been
/// left halfway", about a write the host never let start.
/// </para>
/// </remarks>
public sealed class DrawioEditorImportTest : PlantCanvasBunitContext
{
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static DeviceTypeDto Type() => new("automation", 0, "Automation",
        [new ShapePartDto("M 4 4 H 96 V 96 H 4 Z", "#334155", Filled: false, Rigid: false)],
        [new DevicePortDto("in", 4, 50, "in"), new DevicePortDto("out", 96, 50, "out")],
        new ShapePointDto(50, 50), new ShapePointDto(88, 60));

    private static PlantItemDto Item(Guid id, double x, double y) =>
        new(id, id.ToString()[..4], DeviceTypes.Automation, null, null, null, default, default, 0, null,
            0, 0, 0, 0, default!, x, y, 130, 184, 0);

    private static DrawioWriteHandler HostWithTwoDevices()
    {
        var handler = new DrawioWriteHandler
        {
            Items = [Item(A, 10, 20), Item(B, 500, 20)],
            Types = [Type()],
        };
        handler.Configs[A] = new PlantItemConfigDto { ItemId = A, Name = "A" };
        handler.Configs[B] = new PlantItemConfigDto { ItemId = B, Name = "B" };
        return handler;
    }

    private async Task<IRenderedComponent<DrawioEditor>> RenderWithAsync(DrawioWriteHandler handler)
    {
        var state = new EditorSessionState(new Uri("http://localhost:5580"), handler);
        state.UseKey("test-key");
        Services.AddSingleton(state);
        var cut = Render<DrawioEditor>();
        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());
        return cut;
    }

    /// <summary>A file this Editor could have exported, carrying one device and, if asked, the drawing.</summary>
    private static InputFileContent NativeFile(string? decoration, params Guid[] itemIds) =>
        InputFileContent.CreateFromBinary(
            JsonSerializer.SerializeToUtf8Bytes(new PlantGraphDto(
                [.. itemIds.Select(id => new PlantItemConfigDto { ItemId = id })], [], [], [], decoration)),
            "impianto.json");

    /// <summary>
    /// A file exported BEFORE the drawing travelled in it: the field is not null, it is not there
    /// at all. Built as that export really built it - serialised without the property - because
    /// what is being measured is that reading such a file gives null, and a hand-made DTO with
    /// null in it would measure nothing.
    /// </summary>
    private static InputFileContent FileFromBeforeTheDrawingTravelled(params Guid[] itemIds)
    {
        var json = JsonSerializer.Serialize(new
        {
            Items = itemIds.Select(id => new { ItemId = id }).ToArray(),
            Geometries = Array.Empty<object>(),
            Junctions = Array.Empty<object>(),
            DoorMappings = Array.Empty<object>(),
        });
        Assert.DoesNotContain("Decoration", json, StringComparison.Ordinal);
        return InputFileContent.CreateFromBinary(System.Text.Encoding.UTF8.GetBytes(json), "impianto-di-ieri.json");
    }

    private static void Choose(IRenderedComponent<DrawioEditor> cut, InputFileContent file) =>
        cut.FindComponent<InputFile>().UploadFiles(file);

    private static void Confirm(IRenderedComponent<DrawioEditor> cut) =>
        cut.Find("#plant-import-confirm-button").Click();

    private static string TextOf(IRenderedComponent<DrawioEditor> cut, string selector) =>
        cut.Find(selector).TextContent;

    /// <summary>
    /// <b>The measurement the whole batch turns on.</b> Nothing has been saved on this page yet,
    /// so nobody holds the permit: the import has to ask for it itself, through the page, or the
    /// host refuses it. The count of permit requests is the assertion - a write that went through
    /// without one would have been written by a page that never asked.
    /// </summary>
    [Fact]
    public async Task AnImportRightAfterOpeningAsksForThePermitAndOnlyThenWrites()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);
        Assert.Equal(0, handler.EditLockRequests);

        Choose(cut, NativeFile(decoration: null, A));
        Assert.Empty(handler.GraphPuts);
        Assert.Equal(0, handler.EditLockRequests);

        Confirm(cut);

        Assert.Equal(1, handler.EditLockRequests);
        var imported = Assert.Single(handler.GraphPuts);
        Assert.Equal(A, Assert.Single(imported.Items).ItemId);
    }

    /// <summary>
    /// Row 46: an import that went through is followed by a full reload, and that reload must not
    /// ask "Leave site?" - the canvas holds a drawing of the plant the operator has just chosen to
    /// replace. So once the write is done the page tells the bridge to forget the unsaved work, and
    /// it does so BEFORE the reload.
    /// </summary>
    [Fact]
    public async Task AnImportThatWentThroughForgetsTheUnsavedWorkBeforeTheReload()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);
        var navigation = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        bool? forgottenWhenTheReloadStarted = null;
        navigation.LocationChanged += (_, _) => forgottenWhenTheReloadStarted ??=
            JSInterop.Invocations.Any(call => call.Identifier == "forgetUnsavedWork");

        Choose(cut, NativeFile(decoration: null, A));
        Assert.DoesNotContain(JSInterop.Invocations, call => call.Identifier == "forgetUnsavedWork");

        Confirm(cut);

        Assert.Single(handler.GraphPuts);
        Assert.Single(JSInterop.Invocations, call => call.Identifier == "forgetUnsavedWork");
        Assert.True(forgottenWhenTheReloadStarted is not null,
            "The import went through and the page was not reloaded: this test is not looking at the reload.");
        Assert.True(forgottenWhenTheReloadStarted, "The page was reloaded BEFORE the unsaved work was forgotten: the reload would ask \"Leave site?\".");
    }

    /// <summary>
    /// The forget runs right after a write that WENT THROUGH, so however it fails - here with an
    /// exception nobody listed - the import is still the success it was: the page is reloaded and
    /// no error is put on the dialog (fix round 1 of row 46). Not forgotten only costs one
    /// "Leave site?" too many on that reload.
    /// </summary>
    [Fact]
    public async Task AForgetThatFailsInAnyWayLeavesTheImportASuccessAndStillReloads()
    {
        var bridge = JSInterop.SetupModule("./_content/Wcs.WebEditor/drawio-bridge.js");
        bridge.SetupVoid("forgetUnsavedWork")
            .SetException(new InvalidOperationException("the bridge broke in a way nobody listed"));
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);
        var navigation = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        var reloaded = false;
        navigation.LocationChanged += (_, _) => reloaded = true;

        Choose(cut, NativeFile(decoration: null, A));
        Confirm(cut);

        Assert.Single(handler.GraphPuts);
        Assert.Single(bridge.Invocations["forgetUnsavedWork"]);
        Assert.True(reloaded, "The import went through, the forget failed, and the page was not reloaded.");
        Assert.Empty(cut.FindAll("#plant-io-error"));
    }

    /// <summary>
    /// Row 46, second half (owner's decision of 2026-09-25). The write was ATTEMPTED and its outcome
    /// never came back - the connection dropped, or the host timed out (<c>ImportExport.ConfirmImportAsync</c>'s
    /// own general catch for <c>HttpRequestException</c>/<c>TaskCanceledException</c>/an untyped
    /// <c>WcsApiException</c>): the substitution may or may not have gone through, and that catch
    /// reloads the page UNCONDITIONALLY, because the canvas may be drawing a plant that is no longer
    /// there. That reload must not ask "Leave site?" about a drawing the operator can no longer trust
    /// either way - so the bridge is told to forget the unsaved work before it, exactly as a write
    /// that went through does.
    /// </summary>
    [Fact]
    public async Task AnImportWhoseOutcomeIsUnknownForgetsTheUnsavedWorkBeforeTheReload()
    {
        var handler = HostWithTwoDevices();
        handler.TimeoutRoute = "graph";
        var cut = await RenderWithAsync(handler);
        var navigation = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        bool? forgottenWhenTheReloadStarted = null;
        navigation.LocationChanged += (_, _) => forgottenWhenTheReloadStarted ??=
            JSInterop.Invocations.Any(call => call.Identifier == "forgetUnsavedWork");

        Choose(cut, NativeFile(decoration: null, A));
        Assert.DoesNotContain(JSInterop.Invocations, call => call.Identifier == "forgetUnsavedWork");

        Confirm(cut);

        Assert.Single(JSInterop.Invocations, call => call.Identifier == "forgetUnsavedWork");
        Assert.True(forgottenWhenTheReloadStarted is not null,
            "The import's outcome went unknown and the page was not reloaded: this test is not looking at the reload.");
        Assert.True(forgottenWhenTheReloadStarted, "The page was reloaded BEFORE the unsaved work was forgotten: the reload would ask \"Leave site?\".");
    }

    /// <summary>
    /// The other half of the same decision, and the one that must NOT change: a write REFUSED before
    /// anything was written (409 - work in progress, or 400 - an invalid file; here 409) is not an
    /// unknown outcome. <c>GraphEndpoints</c> validates the whole replacement before touching the
    /// database, so the drawing on the canvas is exactly what it was - unsaved work and all - and
    /// <c>ConfirmImportAsync</c>'s own catch for these two codes does not even reload the page.
    /// </summary>
    [Fact]
    public async Task AnImportRefusedBeforeTheWriteWithAConflictForgetsNothing()
    {
        var handler = HostWithTwoDevices();
        handler.FailRoute = "graph";
        handler.FailureStatus = HttpStatusCode.Conflict;
        var cut = await RenderWithAsync(handler);
        var navigation = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        var reloaded = false;
        navigation.LocationChanged += (_, _) => reloaded = true;

        Choose(cut, NativeFile(decoration: null, A));
        Confirm(cut);

        Assert.Empty(handler.GraphPuts);
        Assert.DoesNotContain(JSInterop.Invocations, call => call.Identifier == "forgetUnsavedWork");
        Assert.False(reloaded, "A 409 was refused before the write, and the page was reloaded anyway.");
        Assert.Contains("Import refused", TextOf(cut, "#plant-io-error"));
    }

    /// <summary>
    /// Fix round 1 (reviewer finding 4, part of the missing coverage): the twin of the 409 case
    /// above with an invalid file (400) instead of work in progress - the OTHER status the row 46
    /// decision names, and the filter would have missed it silently if it had only ever been
    /// written for Conflict.
    /// </summary>
    [Fact]
    public async Task AnImportRefusedBeforeTheWriteWithABadRequestForgetsNothing()
    {
        var handler = HostWithTwoDevices();
        handler.FailRoute = "graph";
        handler.FailureStatus = HttpStatusCode.BadRequest;
        var cut = await RenderWithAsync(handler);
        var navigation = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        var reloaded = false;
        navigation.LocationChanged += (_, _) => reloaded = true;

        Choose(cut, NativeFile(decoration: null, A));
        Confirm(cut);

        Assert.Empty(handler.GraphPuts);
        Assert.DoesNotContain(JSInterop.Invocations, call => call.Identifier == "forgetUnsavedWork");
        Assert.False(reloaded, "A 400 was refused before the write, and the page was reloaded anyway.");
        Assert.Contains("Import refused", TextOf(cut, "#plant-io-error"));
    }

    /// <summary>
    /// Fix round 1 (reviewer finding 4): a THIRD refusal-before-write, added by this same round -
    /// the edit permit lost in the moment between <c>EnsureWritableAsync</c>'s own check and the PUT
    /// actually landing on the host. <c>RequiresEditPermission</c>'s filter answers 423 before the
    /// route's body - and so before the database - ever runs (<c>H/Security/EditPermissionFilter.cs</c>):
    /// the same shape as 409/400, and the same "nothing to forget" as them.
    /// </summary>
    [Fact]
    public async Task AnImportRefusedBeforeTheWriteWithALostPermitForgetsNothing()
    {
        var handler = HostWithTwoDevices();
        handler.FailRoute = "graph";
        handler.FailureStatus = HttpStatusCode.Locked;
        var cut = await RenderWithAsync(handler);
        var navigation = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        var reloaded = false;
        navigation.LocationChanged += (_, _) => reloaded = true;

        Choose(cut, NativeFile(decoration: null, A));
        Confirm(cut);

        Assert.Empty(handler.GraphPuts);
        Assert.DoesNotContain(JSInterop.Invocations, call => call.Identifier == "forgetUnsavedWork");
        Assert.False(reloaded, "A 423 was refused before the write, and the page was reloaded anyway.");
        Assert.Contains("Import refused", TextOf(cut, "#plant-io-error"));
    }

    /// <summary>
    /// Fix round 1 (reviewer finding 4): the catch that forgets was "anything but OutOfMemoryException",
    /// wider than what <c>ImportExport.ConfirmImportAsync</c>'s own general catch recognizes
    /// (<c>HttpRequestException</c>/<c>TaskCanceledException</c>/<c>WcsApiException</c>). An exception
    /// of neither kind - a bug in the write path itself, not a network or API failure - is not one
    /// ImportExport reloads for either, so forgetting for it would be a lie about what happens next:
    /// nothing does, the exception keeps climbing past this page entirely.
    /// </summary>
    [Fact]
    public async Task AWriteThatFailsInAWayNeitherCatchRecognizesForgetsNothing()
    {
        var handler = HostWithTwoDevices();
        handler.GraphPutFailure = new InvalidOperationException("a bug in the write path itself (test)");
        var cut = await RenderWithAsync(handler);

        Choose(cut, NativeFile(decoration: null, A));

        var thrown = await Record.ExceptionAsync(() => cut.InvokeAsync(() => cut.Find("#plant-import-confirm-button").Click()));
        Assert.NotNull(thrown);

        Assert.DoesNotContain(JSInterop.Invocations, call => call.Identifier == "forgetUnsavedWork");
    }

    /// <summary>
    /// And a refused import forgets nothing: the plant was not touched, the drawing stays, and
    /// whatever of it is not saved is still work a closed tab would lose.
    /// </summary>
    [Fact]
    public async Task ARefusedImportForgetsNothing()
    {
        var handler = HostWithTwoDevices();
        handler.GrantEditLock = false;
        var cut = await RenderWithAsync(handler);

        Choose(cut, NativeFile(decoration: null, A));
        Confirm(cut);

        Assert.Empty(handler.GraphPuts);
        Assert.Contains("somebody else has been editing", TextOf(cut, "#plant-io-error"));
        Assert.DoesNotContain(JSInterop.Invocations, call => call.Identifier == "forgetUnsavedWork");
    }

    /// <summary>
    /// A refusal is NOT the "it may have been left halfway" warning: nothing was written, the host
    /// was never asked, and the file the operator chose is still in hand. Saying otherwise would
    /// send him to check a plant nobody touched - and throwing the page away would make him pick
    /// the file again for nothing.
    /// </summary>
    [Fact]
    public async Task WithoutThePermitNothingIsImportedAndTheDialogStaysWithTheReasonOnIt()
    {
        var handler = HostWithTwoDevices();
        handler.GrantEditLock = false;
        var cut = await RenderWithAsync(handler);

        Choose(cut, NativeFile(decoration: null, A));
        Confirm(cut);

        Assert.Empty(handler.GraphPuts);
        Assert.Contains("Read only", TextOf(cut, "#plant-io-error"));
        Assert.Contains("somebody else has been editing", TextOf(cut, "#plant-io-error"));
        // The dialog is still there, with the confirmation live again: the remedy is to press it
        // once more, and a greyed button would make that remedy impossible.
        Assert.False(cut.Find("#plant-import-confirm-button").HasAttribute("disabled"));
    }

    /// <summary>
    /// Row 37 of the queue (the owner's answer of 9/10): a permit that could not be asked for is said
    /// in the Plant file window as in the properties panel - one sentence with its why, and what came
    /// back behind a "?" next to it, where the sentence used to end on the failure's message.
    /// </summary>
    [Fact]
    public async Task APermitThatCannotBeAskedForIsSaidWithTheFailureBehindTheQuestionMark()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        Choose(cut, NativeFile(decoration: null, A));
        handler.HttpFailureRoute = "edit-lock";
        Confirm(cut);

        Assert.Empty(handler.GraphPuts);
        Assert.Equal("The edit permit could not be asked for because the connection to the host failed: nothing was written.",
            TextOf(cut, "#plant-io-error").Trim());
        Assert.StartsWith("HttpRequestException: ", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "plant-io-error"));
        Assert.False(cut.Find("#plant-import-confirm-button").HasAttribute("disabled"));
    }

    /// <summary>The engine's half of the same refusal: a running engine stops the import before the host is asked.</summary>
    [Fact]
    public async Task WithTheEngineRunningNothingIsImported()
    {
        var handler = HostWithTwoDevices();
        handler.NavStatus = NavigatorStatusTypes.Run;
        var cut = await RenderWithAsync(handler);

        Choose(cut, NativeFile(decoration: null, A));
        Confirm(cut);

        Assert.Empty(handler.GraphPuts);
        // Held by the dialog itself since the owner's decision of 27/9 on Pause: it knows the engine's state.
        Assert.Contains("the engine is in Run", TextOf(cut, "#plant-io-error"));
    }

    /// <summary>
    /// The import is the FIFTH writer of this page (<c>PlantWriter.Importing</c>), and the gate
    /// works both ways. Here the drawing's save is held inside its permit request - which is where
    /// the gate is already raised - and the import that arrives meanwhile is refused BY NAME,
    /// without emptying anything.
    /// </summary>
    [Fact]
    public async Task AnImportArrivingWhileTheDrawingIsBeingSavedIsRefusedByNameAndWritesNothing()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);
        Choose(cut, NativeFile(decoration: null, A));

        handler.EditLockGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = cut.InvokeAsync(() => cut.Instance.OnDrawioSaved(DrawingOf(handler)));
        await handler.EditLockGateReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Confirm(cut);

        var refusal = TextOf(cut, "#plant-io-error");
        Assert.Contains("The drawing is being saved", refusal);
        Assert.Contains("the plant was not emptied", refusal);
        Assert.DoesNotContain("properties panel", refusal);
        Assert.Empty(handler.GraphPuts);

        handler.EditLockGate!.SetResult();
        await held;
    }

    /// <summary>
    /// The other way round, which is the one with most to lose: a save that went ahead here would
    /// compute its delta against a plant being emptied underneath it and write it onto the imported
    /// one. It is refused, and the refusal names the import - not one of the other three gestures,
    /// which is what a <c>_</c> branch would have made it say.
    /// </summary>
    [Fact]
    public async Task ADrawingSaveArrivingWhileAnImportIsOnItsWayIsRefusedAndNamesTheImport()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);
        Choose(cut, NativeFile(decoration: null, A));

        handler.GraphPutGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = cut.InvokeAsync(() => cut.Find("#plant-import-confirm-button").Click());
        await handler.GraphPutGateReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSaved(DrawingOf(handler)));

        var refusal = TextOf(cut, "#drawio-save-error");
        Assert.Contains("A whole plant is being imported from a file", refusal);
        Assert.Contains("nothing of the drawing was written", refusal);
        Assert.DoesNotContain("properties panel", refusal);
        // The save wrote NOTHING, and the decoration is what says so: an empty round - a drawing
        // handed back exactly as it was seeded - still writes the drawing's decoration every time,
        // so its absence here is the difference between a save refused and a save that went ahead.
        Assert.DoesNotContain("decoration", handler.Writes);

        handler.GraphPutGate!.SetResult();
        await held;
    }

    /// <summary>
    /// What the confirmation says about the free drawing, and what is really sent. The two files
    /// differ in one thing only - whether the fifth field of the contract is there at all - and
    /// they must produce opposite sentences and opposite bodies, because they do opposite things
    /// to something an operator may have spent hours on.
    /// </summary>
    [Fact]
    public async Task AFileThatCarriesTheDrawingSaysSoAndSendsIt()
    {
        var handler = HostWithTwoDevices();
        handler.Decoration = "<mxCell id=\"già-sul-impianto\"/>";
        var cut = await RenderWithAsync(handler);

        Choose(cut, NativeFile("<mxCell id=\"dal-file\"/>", A));

        var said = TextOf(cut, "#plant-import-decoration");
        Assert.Contains("carries the free drawing", said);
        Assert.Contains("replaced with the one in the file", said);

        Confirm(cut);
        Assert.Equal("<mxCell id=\"dal-file\"/>", Assert.Single(handler.GraphPuts).Decoration);
    }

    /// <summary>The twin of the above, and the case a beta tester really has: a file from yesterday.</summary>
    [Fact]
    public async Task AFileFromBeforeTheDrawingTravelledSaysItDoesNotSpeakAboutItAndSendsNothingOfIt()
    {
        var handler = HostWithTwoDevices();
        handler.Decoration = "<mxCell id=\"disegnato-a-mano\"/>";
        var cut = await RenderWithAsync(handler);

        Choose(cut, FileFromBeforeTheDrawingTravelled(A));

        var said = TextOf(cut, "#plant-import-decoration");
        Assert.Contains("says nothing", said);
        Assert.Contains("stay as they are now", said);

        Confirm(cut);
        Assert.Null(Assert.Single(handler.GraphPuts).Decoration);
        // And the host still has what it had: nothing spoke about the drawing, so nothing moved it.
        Assert.Equal("<mxCell id=\"disegnato-a-mano\"/>", handler.Decoration);
    }

    private static readonly PlcConnectionDto Plc1 = new(1, "Line 1", "10.0.0.1", 2011, "10.0.0.2", 2012);

    /// <summary>A file this Editor could have exported with PLC connections, as every export carries them.</summary>
    private static InputFileContent FileWithPlcConnections(params PlcConnectionDto[] connections) =>
        InputFileContent.CreateFromBinary(
            JsonSerializer.SerializeToUtf8Bytes(new PlantGraphDto([new PlantItemConfigDto { ItemId = A }], [], [], [], null, connections)),
            "impianto.json");

    /// <summary>
    /// The WIRING: the page hands the import the engine's state it shows, so a file - here one that
    /// changes the PLC connections - is said to be refused, and held, while the engine is paused.
    /// </summary>
    [Fact]
    public async Task WithTheEnginePausedTheImportOfAChangedPlcListIsSaidAndHeld()
    {
        var handler = HostWithTwoDevices();
        handler.NavStatus = NavigatorStatusTypes.Pause;
        handler.PlcConnections = [Plc1];
        var cut = await RenderWithAsync(handler);

        Choose(cut, FileWithPlcConnections(Plc1 with { Description = "renamed" }));

        cut.WaitForAssertion(() => Assert.Contains("the engine is in Pause", TextOf(cut, "#plant-import-engine")));
        Assert.True(cut.Find("#plant-import-confirm-button").HasAttribute("disabled"));
        Assert.Empty(handler.GraphPuts);
    }

    /// <summary>
    /// Owner's decision of 27/9 on Pause: even the normal file - the host's own connections - is held in
    /// Pause now (it went through since Task 4c). And the page's write gate refuses it too, at write
    /// time: a click that gets past the held button (bUnit's Click ignores "disabled") reaches the page's
    /// own check, and nothing is sent.
    /// </summary>
    [Fact]
    public async Task WithTheEnginePausedEvenTheHostsOwnPlcListIsHeldAndRefusedAtWriteTime()
    {
        var handler = HostWithTwoDevices();
        handler.NavStatus = NavigatorStatusTypes.Pause;
        handler.PlcConnections = [Plc1];
        var cut = await RenderWithAsync(handler);

        Choose(cut, FileWithPlcConnections(Plc1));

        cut.WaitForAssertion(() => Assert.Contains("the engine is in Pause", TextOf(cut, "#plant-import-engine")));
        Assert.True(cut.Find("#plant-import-confirm-button").HasAttribute("disabled"));
        Assert.Empty(handler.GraphPuts);
    }

    /// <summary>
    /// The page's own check at write time (ImportThroughTheGateAsync): the dialog did not know the
    /// engine was paused (its state is read again with the permit), and the page refuses before sending.
    /// </summary>
    [Fact]
    public async Task AnImportThatReachesTheGateInPauseIsRefusedByThePage()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);
        Choose(cut, FileWithPlcConnections(Plc1));
        handler.NavStatus = NavigatorStatusTypes.Pause;

        Confirm(cut);

        cut.WaitForAssertion(() => Assert.Contains(
            "the engine is paused, and a plant is imported only with the engine stopped",
            TextOf(cut, "#plant-io-error")));
        Assert.Empty(handler.GraphPuts);
    }

    /// <summary>
    /// A drawing built from the plant the fake host holds: the same document the page seeds draw.io
    /// with, handed straight back unchanged - a save that writes nothing of its own, which is what
    /// the two crossing tests above need.
    /// </summary>
    private static string DrawingOf(DrawioWriteHandler handler) =>
        Wcs.WebEditor.Drawio.DrawioDocument.Build(
            new Wcs.WebEditor.Model.PlantGraph([.. handler.Items], [.. handler.Junctions], [.. handler.Types]),
            handler.Decoration);
}
