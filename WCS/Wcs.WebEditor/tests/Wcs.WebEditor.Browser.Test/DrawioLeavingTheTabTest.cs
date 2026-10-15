using System.Text.Json;
using Xunit;
using Xunit.Abstractions;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// Row 46 of the parity inventory: <b>closing or reloading the tab with drawing work that is not on
/// the plant yet makes the browser ask "Leave site?"</b> - and closing it with nothing to lose does
/// not.
/// </summary>
/// <remarks>
/// <para>
/// The WinForms Editor asks Yes/No/Cancel and cancels the close when the save fails. A browser allows
/// neither words of our own nor a Save button at that moment: its own question is all there is, and
/// that difference is declared in the inventory.
/// </para>
/// <para>
/// <b>Both directions are measured, and the quiet ones are the half that is easy to fake.</b> A page
/// that always asked would pass the two tests that expect a question; a page that never asked would
/// pass the four that expect none. Each "does not ask" waits for the page to be CLOSED - see
/// <see cref="DrawioEditorPage.CloseTheTabAsync"/> - so a question that came late is still heard.
/// </para>
/// </remarks>
public sealed class DrawioLeavingTheTabTest(DrawioEditorFixture fixture, ITestOutputHelper output)
    : IClassFixture<DrawioEditorFixture>
{
    private const string SeedHolder = "browser-test-leaving-the-tab";

    private const double DragX = 160;
    private const double DragY = 100;

    [Fact]
    public async Task AnUntouchedPageClosesWithoutAsking()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);

        AssertClosedWithoutAsking(await editor.CloseTheTabAsync(), "a page opened and never touched");
    }

    [Fact]
    public async Task AfterADragTheCloseAsksAndRefusingKeepsThePage()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);
        await DragAsync(editor);

        var closing = await editor.CloseTheTabAsync();
        AssertAsked(closing, "a device dragged and not saved");
        Assert.True(closing.StillOpen,
            "The question was refused and the page went away all the same: the operator said \"stay\" and lost the drawing.");

        // Still the same drawing, with the device where the operator left it: the refusal kept the
        // work, not merely a tab.
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
    }

    [Fact]
    public async Task AfterADragAndASaveThatWroteEverythingTheCloseDoesNotAsk()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);
        await DragAsync(editor);

        await editor.SaveAsync();
        // The premise, read before the close: a save that stopped or refused anything is the NEXT
        // test, and taking one for the other would make this green for the wrong reason.
        var report = await editor.ReportAsync();
        output.WriteLine($"Save report:{Environment.NewLine}{report}");
        Assert.Null(await editor.SaveErrorAsync());
        Assert.Empty(await editor.RefusalsAsync());
        Assert.Empty(await editor.NotWrittenAsync());
        Assert.Empty(await editor.UnconfirmedAsync());
        Assert.Contains(await editor.WrittenAsync(), line => line.Contains(PlantSeed.DraggedName, StringComparison.Ordinal));

        AssertClosedWithoutAsking(await editor.CloseTheTabAsync(), "a drag saved in full");
    }

    [Fact]
    public async Task AfterASaveThatRefusedAPieceTheCloseStillAsks()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);

        // Grouped and then moved: the save refuses to write a grouped device's position (the box is
        // relative to the group), and names both devices - see DrawioGroupedCellsTest. It is a
        // refusal an operator produces with two ordinary gestures.
        await editor.SelectDevicesAsync(PlantSeed.DraggedName, PlantSeed.UntouchedName);
        await editor.GroupSelectionAsync();
        await DragAsync(editor);

        await editor.SaveAsync();
        var report = await editor.ReportAsync();
        output.WriteLine($"Save report:{Environment.NewLine}{report}");
        Assert.Null(await editor.SaveErrorAsync());
        var refusals = await editor.RefusalsAsync();
        Assert.True(refusals.Any(line => line.Contains(PlantSeed.DraggedId.ToString(), StringComparison.OrdinalIgnoreCase)),
            $"The save did not refuse the grouped device's move, so this test is not looking at a partial save.{Environment.NewLine}{report}");

        var closing = await editor.CloseTheTabAsync();
        AssertAsked(closing, "a save that refused part of the drawing");
        Assert.True(closing.StillOpen, "The question was refused and the page went away all the same.");
    }

    [Fact]
    public async Task AfterARenameFromThePanelTheCloseDoesNotAsk()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);

        await editor.SelectDevicesAsync(PlantSeed.DraggedName);
        await editor.WaitForPropertyAsync("Name", PlantSeed.DraggedName);

        const string renamed = "RenamedBeforeLeaving";
        await editor.TypePropertyAsync("Name", renamed);
        await editor.ConfirmPropertiesAsync();
        await editor.WaitForPropertiesWrittenAsync(renamed);
        Assert.Null(await editor.PropertyWriteErrorAsync());

        // The premise, both halves of it: the name is on the PLANT, and the canvas really changed
        // its own model to follow - that model change is the one that must not count as work.
        Assert.Equal(renamed, (await client.GetItemConfigurationAsync(PlantSeed.DraggedId))!.Name);
        await editor.WaitForDeviceAsync(renamed);

        AssertClosedWithoutAsking(await editor.CloseTheTabAsync(), "a rename already written to the plant from the panel");
    }

    /// <summary>
    /// <b>MUST FIX of the final review: re-pointing a shape at a device (row 15) must light the
    /// "changed since last save" mark.</b> Before the fix, <c>DescribeReassociationRenameAsync</c>
    /// called <c>renameCell</c> the same way <c>RenameConfigOnCanvasAsync</c> does - as an echo of a
    /// write already on the plant - but a re-pointing writes only into this page's own
    /// <c>plantItemByDrawnCell</c> map (<c>CellReassociation.WhatItMeans</c>: "Nothing has reached
    /// the plant yet"). The model edit the rename makes on the canvas was therefore suppressed as an
    /// echo and never counted, so a save answering true beforehand could leave the mark down with the
    /// re-pointing still unsaved: closing the tab would lose it silently.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the orphan shape is SEEDED into the plant's own decoration and not dropped live.</b> A
    /// live drop is itself a real model edit (draw.io's own model fires a CHANGE the instant the
    /// birth lands) and would light the mark on its own, before the reassociation gesture even runs -
    /// masking exactly the bug this test is for, since the close would then ask regardless of whether
    /// <c>renameCell</c>'s echo is right or wrong. Seeded into what the plant already holds
    /// (<see cref="PlantSeed.SeedOrphanDeviceCellAsync"/>), the freshly opened page starts clean
    /// around it - the same premise <see cref="AnUntouchedPageClosesWithoutAsking"/> proves for the
    /// two ordinary devices, which does not care what is drawn, only that nothing was touched after
    /// open. The ONLY change made to this page, from open to close, is the one gesture under test.
    /// </para>
    /// <para>
    /// <b>Why the target device has no shape of its own</b> (<see cref="PlantSeed.AddDeviceWithoutAShapeAsync"/>):
    /// <see cref="CellReassociation.WhyNot"/>'s own rule, "already drawn on this canvas, by cell
    /// ...", refuses pointing a shape at a device something else already stands for.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AfterARePointingGestureTheCloseAsks()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        var targetId = Guid.Parse("a1b2c3d4-0000-4000-8000-0000000009f4");
        const string targetName = "ShapelessDeviceForTheLeavingTest";
        await PlantSeed.AddDeviceWithoutAShapeAsync(client, SeedHolder, targetId, targetName, DeviceTypes.Automation);
        const string orphanCellId = "orphan-seeded-for-the-leaving-test";
        const string orphanLabel = "PreSeededOrphanShape";
        await PlantSeed.SeedOrphanDeviceCellAsync(client, SeedHolder, orphanCellId, orphanLabel);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);
        await editor.WaitForTheLabelInTheDrawingAsync(orphanLabel);

        // --- THE GESTURE UNDER TEST: pointing the pre-seeded shape at the device, nothing else
        // touched since the page opened.
        Assert.Equal(1, await editor.SelectCellAsync(orphanCellId));
        await editor.WaitForTheReassociationBoxAsync();
        await editor.WaitForTheReassociationCandidatesAsync("1 device");

        await editor.ReassociateAsync(targetId.ToString());
        var outcome = await editor.WaitForTheReassociationOutcomeAsync();
        output.WriteLine($"Reassociation outcome: {outcome}");
        Assert.True(await editor.TheReassociationWentThroughAsync());
        Assert.Contains("Nothing has reached the plant yet", outcome);
        await editor.WaitForCellLabelAsync(orphanCellId, targetName);

        AssertAsked(
            await editor.CloseTheTabAsync(),
            "a pre-seeded shape re-pointed at a device, nothing saved since the page opened (row 15, MUST FIX of the final review)");
    }

    [Fact]
    public async Task WithALiveFrameDrawnTheCloseDoesNotAsk()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);

        var before = await editor.LiveObjectsAsync();
        var frame = JsonSerializer.Serialize(new
        {
            devices = new[]
            {
                new { cellId = PlantSeed.DraggedId.ToString(), color = "#ff0000", statusName = "In Error", loadUnit = (string?)"LU-7 (7)", loadUnitWidth = 105, loadUnitHeight = 105 },
                new { cellId = PlantSeed.UntouchedId.ToString(), color = "#ffa500", statusName = "Moving", loadUnit = (string?)null, loadUnitWidth = 105, loadUnitHeight = 105 },
            },
            motions = new[] { new { from = PlantSeed.DraggedId.ToString(), to = PlantSeed.UntouchedId.ToString() } },
        });
        Assert.Equal("drawn", await editor.RenderLiveFrameAsync(frame));

        // The premise: colours and the arrow are really on the canvas, or the close below would be
        // about an untouched page.
        var live = await editor.LiveObjectsAsync();
        Assert.True(live[0] > before[0] && live[1] > before[1],
            $"The live frame put nothing on the canvas (overlays {before[0]} -> {live[0]}, arrow paths {before[1]} -> {live[1]}).");

        AssertClosedWithoutAsking(await editor.CloseTheTabAsync(), "a page carrying a live frame (colours and arrows)");
    }

    /// <summary>
    /// <b>A save answer that arrives after a detach and a new attach puts nothing down.</b> The
    /// counters start again from zero at every attach, and an old round's count applied to the new
    /// ones would take unsaved work for saved (review of row 46, fix round 1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The real page cannot be made to hold a save answer across a re-attach on command, so this
    /// drives the bridge MODULE itself, on a page of the host's own origin: a same-origin iframe
    /// stands for draw.io's, with a <c>Draw.loadPlugin</c> handing out a model whose CHANGE listener
    /// the test fires by hand, and a stand-in for the .NET reference whose <c>OnDrawioSaved</c>
    /// answers only when the test says so. The messages come from INSIDE the iframe - a function
    /// built in its realm posts them - because the bridge listens only to its own iframe's window.
    /// </para>
    /// <para>
    /// The question is read the way the browser reads it: a cancellable <c>beforeunload</c>
    /// dispatched on the window, and whether the bridge's handler cancelled it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ASaveAnswerFromBeforeAReattachDoesNotPutTheMarkDown()
    {
        output.WriteLine(fixture.Host.Declaration);
        var context = await fixture.Browser.NewContextAsync();
        try
        {
            var page = await context.NewPageAsync();
            // A page of the host's origin with no Blazor on it, so nothing else attaches the bridge.
            await page.GotoAsync(fixture.Host.PageUrl("_content/Wcs.WebEditor/drawio-bridge.js?top=1"));

            var outcome = await page.EvaluateAsync<bool[]>(
                """
                async (bridgeUrl) => {
                  const bridge = await import(bridgeUrl);
                  const tick = () => new Promise((r) => setTimeout(r, 0));
                  const until = async (what, test) => {
                    for (let i = 0; i < 500; i++) { if (test()) return; await new Promise((r) => setTimeout(r, 10)); }
                    throw new Error('never happened: ' + what);
                  };
                  const iframe = document.createElement('iframe');
                  document.body.appendChild(iframe);

                  const answers = [];
                  const dotnet = {
                    invokeMethodAsync: (name) => name === 'OnDrawioSaved'
                      ? new Promise((resolve) => answers.push(resolve))
                      : Promise.resolve(null),
                  };

                  // One draw.io stand-in per document: the model's CHANGE listener is what the
                  // bridge counts on, and edit() fires it with one change in the edit.
                  const drawio = () => {
                    const w = iframe.contentWindow;
                    const model = { listener: null, addListener(n, l) { this.listener = l; }, removeListener() { this.listener = null; } };
                    w.mxEvent = { CHANGE: 'change' };
                    w.Draw = { loadPlugin: (fn) => fn({ editor: { graph: { getModel: () => model } } }) };
                    return {
                      subscribed: () => model.listener !== null,
                      edit: () => model.listener(model, { getProperty: () => ({ changes: [{}] }) }),
                    };
                  };
                  const post = (message) =>
                    new iframe.contentWindow.Function('m', "parent.postMessage(m, '*')")(JSON.stringify(message));
                  const asks = () => {
                    const e = new Event('beforeunload', { cancelable: true });
                    window.dispatchEvent(e);
                    return e.defaultPrevented;
                  };
                  const attachTo = async (n) => {
                    const loaded = new Promise((r) => iframe.addEventListener('load', r, { once: true }));
                    bridge.attach(iframe, dotnet, bridgeUrl + '?frame=' + n);
                    await loaded;
                    const d = drawio();
                    post({ event: 'init' });
                    await until('the bridge subscribing to the model', d.subscribed);
                    return d;
                  };

                  // First attachment: one edit, and a save handed over with that count.
                  const first = await attachTo(1);
                  first.edit();
                  post({ event: 'save', xml: '<mxGraphModel/>' });
                  await until('the save reaching OnDrawioSaved', () => answers.length === 1);

                  // The page goes and comes back while that round is still running.
                  bridge.detach();
                  const second = await attachTo(2);
                  second.edit();
                  const asksWithTheNewEdit = asks();

                  // The OLD round now answers "wrote everything".
                  answers[0](true);
                  await tick(); await tick();
                  const asksAfterTheOldAnswer = asks();

                  bridge.detach();
                  return [asksWithTheNewEdit, asksAfterTheOldAnswer];
                }
                """,
                "/_content/Wcs.WebEditor/drawio-bridge.js");

            output.WriteLine($"Asks with the new edit: {outcome[0]}; after the old round's answer: {outcome[1]}.");
            Assert.True(outcome[0], "The premise failed: one edit after the re-attach did not make the close ask.");
            Assert.True(outcome[1],
                "A save answer from BEFORE the re-attach put the mark down: the edit made after it is not on the plant, and the close no longer asks.");
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    private async Task DragAsync(DrawioEditorPage editor)
    {
        var (before, after) = await editor.DragDeviceAsync(PlantSeed.DraggedName, DragX, DragY);
        Assert.True(after.X > before.X && after.Y > before.Y,
            $"The drag did not move the shape on the canvas: it was at ({before.X:0.#}, {before.Y:0.#}) and is at " +
            $"({after.X:0.#}, {after.Y:0.#}). The gesture never reached draw.io, so there is no work to lose.");
    }

    private void AssertClosedWithoutAsking(DrawioTabClosing closing, string situation)
    {
        output.WriteLine($"Closing with {situation}: {closing}");
        Assert.False(closing.Asked,
            $"Closing the tab with {situation} made the browser ask \"Leave site?\" ({closing.DialogType}): there " +
            "was nothing to lose, and a question asked every time is a question nobody reads.");
    }

    private void AssertAsked(DrawioTabClosing closing, string situation)
    {
        output.WriteLine($"Closing with {situation}: {closing}");
        Assert.True(closing.Asked,
            $"Closing the tab with {situation} did NOT ask: the page closed and the work went with it.");
        Assert.Equal("beforeunload", closing.DialogType);
    }
}
