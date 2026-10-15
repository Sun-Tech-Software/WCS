using System.Net;
using System.Reflection;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Drawio.Coordination;
using Wcs.WebEditor.Pages;
using Wcs.WebEditor.Session;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// Pins the public DOM seams shared by bUnit, Playwright, and the draw.io bridge while the page is
/// split into child components. Optional panels may be absent, but an extraction must never
/// duplicate an id and the shell anchors must always remain present.
/// </summary>
public sealed class DrawioEditorStructureTest : PlantCanvasBunitContext
{
    public static readonly string[] StableIds =
    [
        "drawio-frame", "drawio-engine-banner", "drawio-decoration-banner",
        "drawio-undrawn", "drawio-creation-report", "drawio-creation-state",
        "drawio-save-report", "drawio-save-round", "drawio-reassociate-round",
        "drawio-plant-file", "drawio-checks", "drawio-property-dock",
        "drawio-property-panel", "drawio-cells", "drawio-reassociate", "drawio-live-status",
        "drawio-ruler-switch", "drawio-pointer-position",
    ];

    [Fact]
    public void StableEditorIdsRemainUniqueAfterComposition()
    {
        using var handler = new EmptyPlantHandler();
        var state = new EditorSessionState(new Uri("http://localhost:5580"), handler);
        state.UseKey("test-key");
        Services.AddSingleton(state);

        var cut = Render<DrawioEditor>();

        foreach (var id in StableIds)
            Assert.InRange(cut.FindAll("#" + id).Count, 0, 1);

        Assert.Single(cut.FindAll("#drawio-frame"));
        Assert.Single(cut.FindAll("#drawio-save-round"));
        Assert.Single(cut.FindAll("#drawio-reassociate-round"));
        // Row 33/35 of the parity inventory: the ruler switch and the pointer position indicator
        // are always on the page (unlike the optional panels above), the same way the shell anchors
        // are - see StableIds' own remark.
        Assert.Single(cut.FindAll("#drawio-ruler-switch"));
        Assert.Single(cut.FindAll("#drawio-pointer-position"));
    }

    [Fact]
    public async Task WriteGateChangesAfterDisposalDoNotRenderThePage()
    {
        var state = new EditorSessionState(new Uri("http://localhost:5580"), new EmptyPlantHandler());
        state.UseKey("test-key");
        Services.AddSingleton(state);
        var cut = Render<DrawioEditor>();
        var gate = Assert.IsType<DrawioPlantWriteGate>(typeof(DrawioEditor)
            .GetField("_writeGate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(cut.Instance));

        cut.Dispose();
        var rendersAfterDisposal = cut.RenderCount;
        Assert.True(gate.TryEnter(DrawioPlantWriter.Drawing, out var lease));
        lease!.Dispose();
        await Task.Yield();

        Assert.Equal(rendersAfterDisposal, cut.RenderCount);
    }

    private sealed class EmptyPlantHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            object response = path switch
            {
                var value when value.EndsWith("/plant/items", StringComparison.Ordinal) =>
                    new List<PlantItemDto>(),
                var value when value.EndsWith("/plant/junctions", StringComparison.Ordinal) =>
                    new List<JunctionDto>(),
                var value when value.EndsWith("/plant/device-types", StringComparison.Ordinal) =>
                    new List<DeviceTypeDto>(),
                // PlantGraph.LoadAsync's fourth read since queue item 21: every configuration.
                var value when value.EndsWith("/plant/items/config", StringComparison.Ordinal) =>
                    new List<PlantItemConfigDto>(),
                var value when value.EndsWith("/plant/decoration", StringComparison.Ordinal) =>
                    new PlantDecorationDto(string.Empty),
                // The live feed's snapshot: before "/status", which it also ends with.
                var value when value.EndsWith("/plant/items/status", StringComparison.Ordinal) =>
                    new List<ItemStatus>(),
                var value when value.EndsWith("/status", StringComparison.Ordinal) =>
                    new EngineStatus(NavigatorStatusTypes.Stop, DefragmentationTypes.None, "test", DateTime.UtcNow),
                _ => throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}"),
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(response)),
            });
        }
    }
}
