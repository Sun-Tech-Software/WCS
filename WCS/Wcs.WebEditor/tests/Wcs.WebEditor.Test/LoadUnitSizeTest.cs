using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Editing;
using Wcs.WebEditor.Model;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// Queue item 21: the size the load unit on board of a device is drawn with. The rule (the WinForms
/// Editor's, <c>DiagramHandler.Private.cs</c> <c>UpdateItemLoadStatus</c>), the graph that keeps one
/// per device, and every command of this page that writes a configuration keeping it right.
/// </summary>
/// <remarks>
/// The expected sizes are written out as numbers, never recomputed through
/// <see cref="LoadUnitSize.Of(int, int)"/>: a test that asked the rule for its own answer could not
/// fail.
/// </remarks>
public sealed class LoadUnitSizeTest
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");
    private static readonly Guid C = Guid.Parse("cccccccc-0000-4000-8000-000000000003");

    private static PlantItemDto Item(Guid id, string name = "Shuttle") =>
        new(id, name, DeviceTypes.Automation, null, null, null, ConnectionTypes.Test,
            default, 0, null, 0, 0, 0, 0, default!, 10, 10, 120, 60, 0);

    private static PlantItemConfigDto Config(Guid id, int width, int height) => new()
    {
        ItemId = id,
        Name = "Shuttle",
        DeviceType = (short)DeviceTypes.Automation,
        LoadUnitImageWidth = width,
        LoadUnitImageHeight = height,
    };

    private static WcsClient NewClient(HttpMessageHandler handler) =>
        new(new WcsClientOptions { BaseAddress = new Uri("http://localhost:5580") }, handler);

    // ---- The rule --------------------------------------------------------------------------------

    [Theory]
    [InlineData(40, 30, 40, 30)]
    [InlineData(1, 1, 1, 1)]
    [InlineData(300, 100, 300, 100)]
    // Only when BOTH are greater than zero: one set and one not is the palette's, not half a size.
    [InlineData(0, 30, 105, 105)]
    [InlineData(40, 0, 105, 105)]
    [InlineData(0, 0, 105, 105)]
    [InlineData(-40, 30, 105, 105)]
    [InlineData(40, -30, 105, 105)]
    public void BothSidesSetOrThePalettesSize(int width, int height, int expectedWidth, int expectedHeight)
    {
        Assert.Equal(new LoadUnitSize(expectedWidth, expectedHeight), LoadUnitSize.Of(width, height));
        Assert.Equal(new LoadUnitSize(expectedWidth, expectedHeight), LoadUnitSize.Of(Config(A, width, height)));
    }

    // ---- The graph -------------------------------------------------------------------------------

    /// <summary>
    /// Loading the plant reads every configuration too, and keeps each device's size: its own when
    /// both sides are set, the palette's when one is not - and the palette's for a device the host
    /// has no size for at all (C, whose configuration the stub makes up without one).
    /// </summary>
    [Fact]
    public async Task LoadingThePlantKeepsEachDevicesSize()
    {
        var host = new DrawioWriteHandler { Items = [Item(A), Item(B), Item(C)] };
        host.Configs[A] = Config(A, 40, 30);
        host.Configs[B] = Config(B, 0, 30);
        var graph = new PlantGraph();

        await graph.LoadAsync(NewClient(host));

        Assert.Contains("GET /api/v1/plant/items/config", host.Requests);
        Assert.Equal(new LoadUnitSize(40, 30), graph.LoadUnitSizeOf(A));
        Assert.Equal(new LoadUnitSize(105, 105), graph.LoadUnitSizeOf(B));
        Assert.Equal(new LoadUnitSize(105, 105), graph.LoadUnitSizeOf(C));
    }

    /// <summary>A reload reads the sizes again: a size somebody else changed arrives with it.</summary>
    [Fact]
    public async Task AReloadReadsTheSizesAgain()
    {
        var host = new DrawioWriteHandler { Items = [Item(A)] };
        host.Configs[A] = Config(A, 40, 30);
        var graph = new PlantGraph();
        await graph.LoadAsync(NewClient(host));
        var revision = graph.LoadUnitSizesRevision;

        host.Configs[A] = Config(A, 70, 50);
        await graph.LoadAsync(NewClient(host));

        Assert.Equal(new LoadUnitSize(70, 50), graph.LoadUnitSizeOf(A));
        Assert.True(graph.LoadUnitSizesRevision > revision, "A reload did not move the sizes' revision.");
    }

    /// <summary>
    /// A configuration written by this page puts its size in at once, and moves the revision the page
    /// watches - but only when the size really changed: a write of another field must not make the
    /// page redraw the canvas for nothing.
    /// </summary>
    [Fact]
    public void AWrittenConfigurationPutsItsSizeInAndMovesTheRevisionOnlyWhenItChanged()
    {
        var graph = new PlantGraph([Item(A)], [], [], [Config(A, 40, 30)]);
        var start = graph.LoadUnitSizesRevision;

        graph.ApplyConfiguration(Config(A, 40, 30));
        Assert.Equal(start, graph.LoadUnitSizesRevision);

        graph.ApplyConfiguration(Config(A, 80, 30));
        Assert.Equal(new LoadUnitSize(80, 30), graph.LoadUnitSizeOf(A));
        Assert.Equal(start + 1, graph.LoadUnitSizesRevision);

        // Back to "not set": the palette's, and that too is a change.
        graph.ApplyConfiguration(Config(A, 0, 0));
        Assert.Equal(new LoadUnitSize(105, 105), graph.LoadUnitSizeOf(A));
        Assert.Equal(start + 2, graph.LoadUnitSizesRevision);
    }

    /// <summary>
    /// The page keeps ONE graph and adopts the one a save or an import read again
    /// (<see cref="PlantGraph.Adopt"/>): the sizes must come along, or the canvas would fall back to
    /// the palette's after every save.
    /// </summary>
    [Fact]
    public void AdoptingAnotherGraphTakesItsSizes()
    {
        var kept = new PlantGraph([Item(A)], [], [], [Config(A, 40, 30)]);
        var read = new PlantGraph([Item(A)], [], [], [Config(A, 70, 50)]);
        var revision = kept.LoadUnitSizesRevision;

        kept.Adopt(read);

        Assert.Equal(new LoadUnitSize(70, 50), kept.LoadUnitSizeOf(A));
        Assert.True(kept.LoadUnitSizesRevision > revision, "Adopting did not move the sizes' revision.");
    }

    // ---- Every command that writes a configuration -----------------------------------------------

    [Fact]
    public async Task EditingAConfigurationPutsTheWrittenSizeInAndUndoPutsTheOldOneBack()
    {
        var handler = new PlantMutationHandler();
        var client = NewClient(handler);
        var before = Config(A, 40, 30);
        handler.Configs[A] = before;
        var graph = new PlantGraph([Item(A)], [], [], [before]);
        var command = new EditItemConfigCommand(before, Config(A, 90, 60));

        await command.ApplyAsync(client, graph);
        Assert.Equal(new LoadUnitSize(90, 60), graph.LoadUnitSizeOf(A));

        await command.UndoAsync(client, graph);
        Assert.Equal(new LoadUnitSize(40, 30), graph.LoadUnitSizeOf(A));
    }

    /// <summary>
    /// Written but not read back (the device gone right after the PUT): the write DID go through, so
    /// the size written is the one to draw - the page says "written, not read back", not "not written".
    /// </summary>
    [Fact]
    public async Task AConfigurationWrittenButNotReadBackStillPutsItsSizeIn()
    {
        var host = new DrawioWriteHandler { Items = [Item(A)], HideSingleItemReads = true };
        var client = NewClient(host);
        var before = Config(A, 40, 30);
        host.Configs[A] = before;
        var graph = new PlantGraph([Item(A)], [], [], [before]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => new EditItemConfigCommand(before, Config(A, 90, 60)).ApplyAsync(client, graph));

        Assert.Equal(new LoadUnitSize(90, 60), graph.LoadUnitSizeOf(A));
    }

    [Fact]
    public async Task LinkingACellPutsTheWrittenSizeInAndUndoPutsTheOldOneBack()
    {
        var handler = new PlantMutationHandler();
        var client = NewClient(handler);
        var before = Config(A, 40, 30);
        handler.Configs[A] = before;
        var graph = new PlantGraph([Item(A)], [], [], [before]);
        var after = Config(A, 90, 60);
        after.LinkedCellId = 5m;
        var command = new LinkBufferCellCommand(before, after, [], new DoorMappingLink(A, "A5", 5m));

        await command.ApplyAsync(client, graph);
        Assert.Equal(new LoadUnitSize(90, 60), graph.LoadUnitSizeOf(A));

        await command.UndoAsync(client, graph);
        Assert.Equal(new LoadUnitSize(40, 30), graph.LoadUnitSizeOf(A));
    }

    [Fact]
    public async Task UnlinkingACellPutsTheWrittenSizeInAndUndoPutsTheOldOneBack()
    {
        var handler = new PlantMutationHandler();
        var client = NewClient(handler);
        var mapping = new DoorMappingDto(0, A, "A5", 5m, "A", 0, false);
        handler.DoorMappings.Add(mapping);
        var before = Config(A, 40, 30);
        before.LinkedCellId = 5m;
        handler.Configs[A] = before;
        var graph = new PlantGraph([Item(A)], [], [], [before]);
        var command = new UnlinkBufferCellCommand(before, Config(A, 90, 60), [mapping]);

        await command.ApplyAsync(client, graph);
        Assert.Equal(new LoadUnitSize(90, 60), graph.LoadUnitSizeOf(A));

        await command.UndoAsync(client, graph);
        Assert.Equal(new LoadUnitSize(40, 30), graph.LoadUnitSizeOf(A));
    }

    [Fact]
    public async Task CreatingADevicePutsItsSizeIn()
    {
        var handler = new PlantMutationHandler();
        var graph = new PlantGraph();

        await new CreateItemCommand(Config(A, 90, 60), new PlantItemGeometryDto(A, 130, 70, 120, 60, 0), "Automation")
            .ApplyAsync(NewClient(handler), graph);

        Assert.Equal(new LoadUnitSize(90, 60), graph.LoadUnitSizeOf(A));
    }

    [Fact]
    public async Task DuplicatingADevicePutsTheCopysSizeIn()
    {
        var handler = new PlantMutationHandler();
        var graph = new PlantGraph();

        await new DuplicateItemCommand(Config(B, 90, 60), new PlantItemGeometryDto(B, 150, 90, 120, 60, 0), "Shuttle")
            .ApplyAsync(NewClient(handler), graph);

        Assert.Equal(new LoadUnitSize(90, 60), graph.LoadUnitSizeOf(B));
    }

    /// <summary>
    /// A device deleted and brought back by undo returns with the configuration the command held -
    /// here a size the graph no longer had (the plant was read again without it in between).
    /// </summary>
    [Fact]
    public async Task ADeviceBroughtBackByUndoGetsItsSizeBack()
    {
        var handler = new PlantMutationHandler();
        var client = NewClient(handler);
        var config = Config(A, 90, 60);
        var geometry = new PlantItemGeometryDto(A, 130, 70, 120, 60, 0);
        await client.CreateItemAsync(config);
        var created = (await client.GetItemAsync(A))!;
        await client.SetItemGeometryAsync(GeometryWriter.ToUpdate(geometry, created.GeometryVersion));
        var graph = new PlantGraph([(await client.GetItemAsync(A))!], [], []);
        var command = new DeleteItemCommand(config, geometry, []);
        await command.ApplyAsync(client, graph);
        Assert.Equal(new LoadUnitSize(105, 105), graph.LoadUnitSizeOf(A));

        await command.UndoAsync(client, graph);

        Assert.Equal(new LoadUnitSize(90, 60), graph.LoadUnitSizeOf(A));
    }
}
