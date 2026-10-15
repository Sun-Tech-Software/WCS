using System.Text;
using Xunit;
using Xunit.Abstractions;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// Queue item 13: a plant as large as a real one - larger: DEMOPLANT has 51 devices, this one
/// 152 with 294 junctions - is saved from draw.io's own button and reaches the host, the engine
/// starts from its drawing, and a paste of all of it reaches the page. The first two hand the WHOLE
/// drawing from the browser to the page's circuit (the save, and the unsaved-work check before a
/// start), the third tells the page of every cell it brings, and all three are over the ceiling
/// SignalR puts on one message from the browser by default (32 KB).
/// </summary>
/// <remarks>
/// <para>
/// <b>What happened before the fix</b>, read off the red of these very tests: SignalR refused the
/// message on the host ("The maximum message size of 32768B was exceeded", written at Debug and so
/// invisible under the host's own logging) and closed the connection; the browser logged "Connection
/// disconnected with error"; the circuit came back by itself, but the call the message carried was
/// gone - the save round never ended, the paste was never heard of, and the start said "It cannot be
/// known whether the drawing holds unsaved work (A task was canceled.)".
/// </para>
/// <para>
/// Every message of the circuit is recorded (<see cref="DrawioEditorPage.CircuitFrames"/>) and
/// written to the output with its size, with the host's last lines: the list of what grows with the
/// plant is read from here, not guessed.
/// </para>
/// </remarks>
public sealed class DrawioLargeDrawingTest(DrawioEditorFixture fixture, ITestOutputHelper output) : IClassFixture<DrawioEditorFixture>
{
    private const string SeedHolder = "browser-test-large-drawing";

    /// <summary>Three times DEMOPLANT's 51 devices, for headroom: a customer plant grows.</summary>
    private const int GridDevices = 150;

    private const int GridColumns = 15;

    private static readonly int FreeDevice = PlantSeed.GridFreeDevice(GridDevices, GridColumns);

    /// <summary>
    /// The ceiling the page sets on one message from the browser (<c>WebEditorExtensions.CircuitMessageCeiling</c>,
    /// written again here because this project drives a built host and does not reference the Editor):
    /// the drawing has to be over it, or a drawing sent whole in one message would pass too and the
    /// save would prove the raised ceiling instead of the stream.
    /// </summary>
    private const int PageCircuitCeiling = 320 * 1024;

    /// <summary>
    /// The largest message from the browser a save or a start may send: a piece of a streamed drawing
    /// is at most 50 KB (Blazor's own ceiling on a piece, measured 50,722 bytes with the envelope), and
    /// nothing else they send comes near it. A larger one is the drawing crossing whole.
    /// </summary>
    private const int LargestMessageOfASaveOrAStart = 70 * 1024;

    /// <summary>Frames at least this large are written to the output; the rest are small by construction.</summary>
    private const int FramesWorthListing = 2 * 1024;

    [Fact]
    public async Task ALargeDrawingSavesFromDrawiosButton()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        await PlantSeed.AddAGridOfDrawnDevicesAsync(client, SeedHolder, GridDevices, GridColumns);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);
        await editor.WaitForDeviceAsync(PlantSeed.GridDeviceName(GridDevices));
        await editor.RecordReturnedDrawingAsync();
        output.WriteLine($"markedCells() answer on this drawing: {await editor.MarkedCellsAnswerLengthAsync():N0} characters of JSON.");

        // Some work to save: the real gesture, on the device in the middle of the grid that no
        // junction touches (the seed's own two lie at the left edge, under draw.io's Shapes window).
        var (before, after) = await editor.DragDeviceAsync(PlantSeed.GridDeviceName(FreeDevice), 20, 15);
        Assert.True(after.X > before.X && after.Y > before.Y,
            $"The drag did not move the shape on the canvas: it was at ({before.X:0.#}, {before.Y:0.#}), it is at " +
            $"({after.X:0.#}, {after.Y:0.#}).");

        try
        {
            await editor.SaveAsync();
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"{ex.Message}{Environment.NewLine}{await WhatTheCircuitCarriedAsync(editor)}", ex);
        }
        finally
        {
            output.WriteLine(await WhatTheCircuitCarriedAsync(editor));
        }

        // The test measures what it claims only on a drawing well over the circuit's default ceiling.
        var drawing = await editor.LastReturnedDrawingAsync();
        output.WriteLine($"The drawing handed over: {Encoding.UTF8.GetByteCount(drawing):N0} bytes of UTF-8, {drawing.Length:N0} characters.");
        Assert.True(Encoding.UTF8.GetByteCount(drawing) > PageCircuitCeiling,
            $"The drawing handed over is only {Encoding.UTF8.GetByteCount(drawing):N0} bytes: not the large drawing this " +
            "test is about.");
        AssertTheDrawingNeverCrossedWhole(editor, "the save");

        Assert.Null(await editor.SaveErrorAsync());
        var moved = await PlantSeed.ReadGeometryAsync(client, PlantSeed.GridDeviceId(FreeDevice));
        var seeded = PlantSeed.GridDeviceGeometry(FreeDevice, GridColumns);
        Assert.True(moved.X > seeded.X && moved.Y > seeded.Y,
            $"The save through draw.io's button did not write the move: the device is at ({moved.X}, {moved.Y}), " +
            $"seeded at ({seeded.X}, {seeded.Y}). {await editor.ReportAsync()}");
    }

    [Fact]
    public async Task TheEngineStartsFromALargeDrawing()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        await PlantSeed.AddAGridOfDrawnDevicesAsync(client, SeedHolder, GridDevices, GridColumns);

        try
        {
            await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
            await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);
            await editor.WaitForDeviceAsync(PlantSeed.GridDeviceName(GridDevices));

            // The drawing is untouched: the unsaved-work check reads the WHOLE drawing off draw.io
            // (the bridge's currentDrawing) and finds nothing to write.
            await editor.ClickAsync("#drawio-engine-start");
            await editor.ClickAsync("#drawio-engine-confirm");
            try
            {
                await editor.WaitForTextAsync("#drawio-engine-state", "Engine: Run");
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException(
                    $"{ex.Message}{Environment.NewLine}{await WhatTheCircuitCarriedAsync(editor)}", ex);
            }
            finally
            {
                output.WriteLine(await WhatTheCircuitCarriedAsync(editor));
            }
            Assert.Equal(NavigatorStatusTypes.Run, (await client.GetStatusAsync()).NavStatus);
            AssertTheDrawingNeverCrossedWhole(editor, "the start");

            await editor.ClickAsync("#drawio-engine-stop");
            await editor.ClickAsync("#drawio-engine-confirm");
            await editor.WaitForTextAsync("#drawio-engine-state", "Engine: Stop");
        }
        finally
        {
            await TestHostEngine.LeaveStoppedAsync(client, output);
        }
    }

    /// <summary>
    /// A paste of the whole drawing tells the page of every cell it brings in ONE message
    /// (<c>OnDrawioCellsAdded</c>: measured 66,331 bytes for these 446 cells), over SignalR's default
    /// ceiling and under the one the page sets (<c>WebEditorExtensions.CircuitMessageCeiling</c>). The
    /// page hears of all of them: every copy of a plant device is reported as not created - a copy
    /// carries no device type - and the plant is unchanged.
    /// </summary>
    [Fact]
    public async Task APasteOfTheWholeLargeDrawingReachesThePage()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        await PlantSeed.AddAGridOfDrawnDevicesAsync(client, SeedHolder, GridDevices, GridColumns);
        var devices = 2 + GridDevices;

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);
        await editor.WaitForDeviceAsync(PlantSeed.GridDeviceName(GridDevices));

        await editor.SelectEverythingAndCopyAsync(devices + PlantSeed.GridJunctions(GridDevices));
        await editor.PasteAsync();
        try
        {
            await editor.WaitForTheCreationReportAsync();
            await editor.WaitForCreationIdleAsync();
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"{ex.Message}{Environment.NewLine}{await WhatTheCircuitCarriedAsync(editor)}", ex);
        }
        finally
        {
            output.WriteLine(await WhatTheCircuitCarriedAsync(editor));
        }

        var notCreated = await editor.NotCreatedAsync();
        Assert.Equal(devices, notCreated.Count(line => line.Contains("wcsType", StringComparison.Ordinal)));
        Assert.Equal(devices, (await client.GetPlantGraphAsync()).Items.Count);
    }

    /// <summary>
    /// The bridge holds a drawing handed over for a save only while the page's answer is pending: the
    /// number gives the drawing back while the round runs, and no bytes once the answer came, once the
    /// call failed, or once the bridge was detached. Driven on the bridge MODULE, on a page of the
    /// host's own origin with no Blazor on it, as <c>DrawioLeavingTheTabTest</c> does: a same-origin
    /// iframe stands for draw.io's, and a stand-in for the .NET reference answers when the test says.
    /// </summary>
    [Fact]
    public async Task TheBridgeLetsGoOfAHandedOverDrawingWhenTheRoundEnds()
    {
        output.WriteLine(fixture.Host.Declaration);
        var context = await fixture.Browser.NewContextAsync();
        try
        {
            var page = await context.NewPageAsync();
            await page.GotoAsync(fixture.Host.PageUrl("_content/Wcs.WebEditor/drawio-bridge.js?top=1"));

            var held = await page.EvaluateAsync<int[]>(
                """
                async (bridgeUrl) => {
                  const bridge = await import(bridgeUrl);
                  const until = async (what, test) => {
                    for (let i = 0; i < 500; i++) { if (test()) return; await new Promise((r) => setTimeout(r, 10)); }
                    throw new Error('never happened: ' + what);
                  };
                  const settled = (promise) => promise.then(() => {}, () => {});
                  const iframe = document.createElement('iframe');
                  document.body.appendChild(iframe);

                  const rounds = [];
                  const dotnet = {
                    invokeMethodAsync: (name, drawing) => {
                      if (name !== 'OnDrawioSaved') return Promise.resolve(null);
                      let answer, fail;
                      const promise = new Promise((resolve, reject) => { answer = resolve; fail = reject; });
                      rounds.push({ drawing, answer, fail, promise });
                      return promise;
                    },
                  };
                  const post = (message) =>
                    new iframe.contentWindow.Function('m', "parent.postMessage(m, '*')")(JSON.stringify(message));
                  const bytes = (drawing) => bridge.handedOverDrawing(drawing).length;
                  const save = async (n) => {
                    post({ event: 'save', xml: '<mxfile>drawing ' + n + '</mxfile>' });
                    await until('save ' + n + ' reaching OnDrawioSaved', () => rounds.length === n);
                    return rounds[n - 1];
                  };

                  const loaded = new Promise((r) => iframe.addEventListener('load', r, { once: true }));
                  bridge.attach(iframe, dotnet, bridgeUrl + '?frame=1');
                  await loaded;

                  // Answered: held while pending, let go once the answer came.
                  const first = await save(1);
                  const whilePending = bytes(first.drawing);
                  first.answer(true);
                  await settled(first.promise);
                  await new Promise((r) => setTimeout(r, 0));
                  const afterTheAnswer = bytes(first.drawing);

                  // Failed: let go as well.
                  const second = await save(2);
                  const secondWhilePending = bytes(second.drawing);
                  second.fail(new Error('the circuit is gone (test)'));
                  await settled(second.promise);
                  await new Promise((r) => setTimeout(r, 0));
                  const afterTheFailure = bytes(second.drawing);

                  // Detached with a round still pending: let go at the detach.
                  const third = await save(3);
                  const thirdWhilePending = bytes(third.drawing);
                  bridge.detach();
                  const afterTheDetach = bytes(third.drawing);

                  return [whilePending, afterTheAnswer, secondWhilePending, afterTheFailure, thirdWhilePending, afterTheDetach];
                }
                """,
                "/_content/Wcs.WebEditor/drawio-bridge.js");

            output.WriteLine($"Bytes held (pending, answered, pending, failed, pending, detached): {string.Join(", ", held)}.");
            Assert.All(new[] { held[0], held[2], held[4] }, pending => Assert.True(pending > 0,
                "The premise failed: a drawing handed over is not held while its round is pending."));
            Assert.Equal(0, held[1]);
            Assert.Equal(0, held[3]);
            Assert.Equal(0, held[5]);
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    /// <summary>
    /// No message from the browser so far was large enough to be the drawing in one piece: what went
    /// across went as a stream (see <see cref="LargestMessageOfASaveOrAStart"/>).
    /// </summary>
    private static void AssertTheDrawingNeverCrossedWhole(DrawioEditorPage editor, string what)
    {
        var whole = editor.CircuitFrames().Where(f => f.FromTheBrowser && f.Bytes > LargestMessageOfASaveOrAStart).ToList();
        Assert.True(whole.Count == 0,
            $"During {what} the browser sent {whole.Count} message(s) over {LargestMessageOfASaveOrAStart:N0} bytes - the " +
            $"drawing crossed the circuit whole instead of as a stream:{Environment.NewLine}" +
            string.Join(Environment.NewLine, whole));
    }

    /// <summary>
    /// The circuit's messages worth reading - the large ones, both ways, largest first - and the
    /// host's last lines: together, what went over the wire and what the server made of it.
    /// </summary>
    private Task<string> WhatTheCircuitCarriedAsync(DrawioEditorPage editor)
    {
        var frames = editor.CircuitFrames();
        var text = new StringBuilder();
        text.AppendLine($"Circuit messages: {frames.Count(f => f.FromTheBrowser)} from the browser (largest " +
                        $"{frames.Where(f => f.FromTheBrowser).Select(f => f.Bytes).DefaultIfEmpty().Max():N0} B), " +
                        $"{frames.Count(f => !f.FromTheBrowser)} to it (largest " +
                        $"{frames.Where(f => !f.FromTheBrowser).Select(f => f.Bytes).DefaultIfEmpty().Max():N0} B). " +
                        $"Those of {FramesWorthListing:N0} B or more:");
        foreach (var frame in frames.Where(f => f.Bytes >= FramesWorthListing).OrderByDescending(f => f.Bytes))
            text.AppendLine($"  {frame}");
        text.AppendLine("The host's last lines:");
        text.AppendLine(fixture.Host.LogTail());
        return Task.FromResult(text.ToString());
    }
}
