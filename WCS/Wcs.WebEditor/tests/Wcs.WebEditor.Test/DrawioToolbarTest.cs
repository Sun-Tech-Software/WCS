using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using System.Text.Json;
using Wcs.WebEditor.Checks;
using Wcs.WebEditor.Drawio.Components;
using Wcs.WebEditor.Drawio.Coordination;
using Wcs.WebEditor.Io;
using Wcs.WebEditor.Model;
using Xunit;

namespace Wcs.WebEditor.Test;

public sealed class DrawioToolbarTest : PlantCanvasBunitContext
{
    private static readonly DrawioWindowPlace PlantFilePlace = new(40, 140, 720, 460);

    [Fact]
    public void PlantFileUsesContentSizingWithoutAManualResizeGrip()
    {
        var cut = Render<DrawioPlantFile>(parameters => parameters
            .Add(p => p.Open, true)
            .Add(p => p.Place, PlantFilePlace));

        var window = cut.Find("#drawio-plant-file");
        Assert.Contains("drawio-window-fixed", window.ClassList);
        Assert.Empty(cut.FindAll("#drawio-plant-file > .drawio-window-grip"));
    }

    [Fact]
    public void PlantFileAsksTheWindowModuleForAutomaticNonResizableSizing()
    {
        JSInterop.Mode = JSRuntimeMode.Strict;
        var module = JSInterop.SetupModule(DrawioWindow.ModulePath);
        module.SetupVoid("attach", _ => true).SetVoidResult();
        module.SetupVoid("show", _ => true).SetVoidResult();
        module.SetupVoid("sizeToContent", _ => true).SetVoidResult();
        module.SetupVoid("detach", _ => true).SetVoidResult();

        Render<DrawioPlantFile>(parameters => parameters
            .Add(p => p.Open, true)
            .Add(p => p.Place, PlantFilePlace));

        var attach = Assert.Single(module.Invocations["attach"]);
        Assert.Equal(4, attach.Arguments.Count);
        var options = JsonSerializer.Serialize(attach.Arguments[3]);
        Assert.Contains("\"resizable\":false", options, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"autoSize\":true", options, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlantFileSectionKeepsItsDomAndForwardsTheWriteBoundary()
    {
        var graph = new PlantGraph([], [], []);
        var calls = 0;
        Func<Func<Task>, Task<DrawioSaid?>> through = async write =>
        {
            calls++;
            await write();
            return null;
        };

        var cut = Render<DrawioPlantFile>(parameters => parameters
            .Add(p => p.Graph, graph)
            .Add(p => p.WriteThroughThePage, through));

        Assert.Single(cut.FindAll("#drawio-plant-file"));
        Assert.Single(cut.FindAll("#drawio-plant-file-toggle"));
        Assert.Single(cut.FindAll("#drawio-plant-file-commands"));
        var import = cut.FindComponent<ImportExport>();
        var forwarded = Assert.IsType<Func<Func<Task>, Task<DrawioSaid?>>>(import.Instance.WriteThroughThePage);
        var written = false;
        Assert.Null(await forwarded(() => { written = true; return Task.CompletedTask; }));
        Assert.True(written);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ChecksDockKeepsItsDomAndForwardsEveryCallbackOnce()
    {
        var graph = new PlantGraph([], [], []);
        var targetId = Guid.NewGuid();
        IReadOnlySet<Guid>? highlighted = null;
        bool? running = null;
        Guid? visited = null;

        var cut = Render<DrawioChecksDock>(parameters => parameters
            .Add(p => p.Graph, graph)
            .Add(p => p.Summary, "Plant checks")
            .Add(p => p.Targets, [new DrawioCheckTarget(targetId, "A")])
            .Add(p => p.OnCheckedIsolatedDevices,
                EventCallback.Factory.Create<IReadOnlySet<Guid>>(this, value => highlighted = value))
            .Add(p => p.OnRunningChanged,
                EventCallback.Factory.Create<bool>(this, value => running = value))
            .Add(p => p.OnGoToDevice,
                EventCallback.Factory.Create<Guid>(this, value => visited = value)));

        Assert.Single(cut.FindAll("#drawio-checks"));
        Assert.Single(cut.FindAll("#drawio-checks-goto"));

        var checks = cut.FindComponent<ChecksPanel>();
        var ids = new HashSet<Guid> { targetId };
        await checks.Instance.OnHighlightedItemIdsChanged.InvokeAsync(ids);
        // On the renderer's dispatcher, as ChecksPanel raises it in the page: since the fix round of
        // 29/9 the dock keeps it too (the running check's dot), and a component re-renders only there.
        await cut.InvokeAsync(() => checks.Instance.ConfigCheckRunningChanged.InvokeAsync(true));
        await cut.Find("button.drawio-checks-target").ClickAsync(new());

        Assert.Same(ids, highlighted);
        Assert.True(running);
        Assert.Single(cut.FindAll("#drawio-checks-toggle .drawio-badge-running"));
        Assert.Equal(targetId, visited);
    }

    /// <summary>
    /// The plant's file waits for the plant, as it did in the band: until the page has it, the
    /// section shows only that it is loading, and no import can be started on a plant nobody has read.
    /// </summary>
    [Fact]
    public void PlantFileSectionOffersItsCommandsOnlyAfterTheGraphExists()
    {
        var cut = Render<DrawioPlantFile>();
        Assert.Single(cut.FindAll("#drawio-plant-file"));
        Assert.Empty(cut.FindComponents<ImportExport>());
        Assert.Empty(cut.FindAll("#plant-import-input"));
        Assert.Single(cut.FindAll("#drawio-plant-file-loading"));

        var loaded = Render<DrawioPlantFile>(parameters => parameters
            .Add(p => p.Graph, new PlantGraph([], [], [])));

        Assert.Single(loaded.FindComponents<ImportExport>());
        Assert.Empty(loaded.FindAll("#drawio-plant-file-loading"));
    }

    /// <summary>The band keeps only the checks: the plant's file moved into the page's strip on 27/9.</summary>
    [Fact]
    public void ToolbarComposesOnlyTheChecksAndOnlyAfterTheGraphExists()
    {
        var cut = Render<DrawioToolbar>();
        Assert.Empty(cut.FindAll("#drawio-checks"));

        var loaded = Render<DrawioToolbar>(parameters => parameters
            .Add(p => p.Graph, new PlantGraph([], [], []))
            .Add(p => p.ChecksSummary, "Plant checks"));

        Assert.Single(loaded.FindAll("#drawio-checks"));
        Assert.Empty(loaded.FindAll("#drawio-plant-file"));
        Assert.Empty(loaded.FindComponents<ImportExport>());
    }
}
