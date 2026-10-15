using Bunit;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Wms;
using Xunit;

namespace Wcs.WebEditor.Test;

/// <summary>
/// <c>LinkedCells</c> da solo (compito 11): come <c>DoorMappingsTest</c> per <c>DoorMappings</c> - non
/// tocca <c>PlantCanvas</c> ne' un host. Il cablaggio e' provato a parte in
/// <c>PlantCanvasCellsPanelTest</c>.
/// </summary>
public sealed class LinkedCellsTest : PlantCanvasBunitContext
{
    private static readonly Guid ItemId = Guid.NewGuid();

    private static WmsCell CellWithDoorA(decimal id) => new(id, "W1", "A", 1, 1, $"A{id}", null, "descrizione");
    private static WmsCell CellWithoutDoorA(decimal id) => new(id, "W1", "A", 1, 1, null, $"B{id}", "descrizione");

    [Fact]
    public void ZeroOrNullCurrentCellIdShowsNoCellLinked()
    {
        var cut = Render<LinkedCells>(p => p
            .Add(x => x.ItemId, ItemId)
            .Add(x => x.Cells, [])
            .Add(x => x.CurrentCellId, 0m));

        Assert.Contains("No linked cell", cut.Find(".wms-linked-cell-current").TextContent);
    }

    [Fact]
    public void ANonZeroCurrentCellIdShowsItsValue()
    {
        var cut = Render<LinkedCells>(p => p
            .Add(x => x.ItemId, ItemId)
            .Add(x => x.Cells, [])
            .Add(x => x.CurrentCellId, 7m));

        Assert.Contains("7", cut.Find(".wms-linked-cell-current").TextContent);
    }

    /// <summary>Una cella senza porta A non compare: senza porta A non c'e' codice da mandare a LinkBufferCellAsync, come OpenCellToLink dell'Editor WinForms scarta una riga senza CODICE_PORTA_A.</summary>
    [Fact]
    public void ACellWithoutDoorAIsNotShownAsLinkable()
    {
        var cut = Render<LinkedCells>(p => p
            .Add(x => x.ItemId, ItemId)
            .Add(x => x.Cells, [CellWithDoorA(1), CellWithoutDoorA(2)])
            .Add(x => x.CurrentCellId, (decimal?)null));

        Assert.NotEmpty(cut.FindAll("tr[data-cell-id='1']"));
        Assert.Empty(cut.FindAll("tr[data-cell-id='2']"));
    }

    [Fact]
    public void LinkIsDisabledUntilACellIsPickedAndInvokesOnLinkWithTheChosenCell()
    {
        var cell = CellWithDoorA(1);
        WmsCell? linked = null;
        var cut = Render<LinkedCells>(p => p
            .Add(x => x.ItemId, ItemId)
            .Add(x => x.Cells, [cell])
            .Add(x => x.CurrentCellId, (decimal?)null)
            .Add(x => x.CanEdit, true)
            .Add(x => x.OnLink, (WmsCell c) => linked = c));

        Assert.True(cut.Find("#linked-cells-link-button").HasAttribute("disabled"));

        cut.Find("#linked-cell-1").Change(true);
        Assert.False(cut.Find("#linked-cells-link-button").HasAttribute("disabled"));
        cut.Find("#linked-cells-link-button").Click();

        Assert.Equal(cell, linked);
    }

    [Fact]
    public void UnlinkIsDisabledWhenThereIsNoCurrentLinkAndInvokesOnUnlinkOtherwise()
    {
        var unlinked = false;
        var cut = Render<LinkedCells>(p => p
            .Add(x => x.ItemId, ItemId)
            .Add(x => x.Cells, [])
            .Add(x => x.CurrentCellId, 0m)
            .Add(x => x.CanEdit, true)
            .Add(x => x.OnUnlink, () => unlinked = true));

        Assert.True(cut.Find("#linked-cells-unlink-button").HasAttribute("disabled"));

        cut.Render(p => p.Add(x => x.CurrentCellId, 5m));
        Assert.False(cut.Find("#linked-cells-unlink-button").HasAttribute("disabled"));
        cut.Find("#linked-cells-unlink-button").Click();

        Assert.True(unlinked);
    }

    [Fact]
    public void NullAutomaticCellsRendersNoSectionAtAllNotAFakeEmptyList()
    {
        var cut = Render<LinkedCells>(p => p
            .Add(x => x.ItemId, ItemId)
            .Add(x => x.Cells, [])
            .Add(x => x.AutomaticCells, (IReadOnlyList<AutomaticCell>?)null));

        Assert.Empty(cut.FindAll(".wms-automatic-cells"));
    }

    [Fact]
    public void AnEmptyAutomaticCellsListSaysSoExplicitly()
    {
        var cut = Render<LinkedCells>(p => p
            .Add(x => x.ItemId, ItemId)
            .Add(x => x.Cells, [])
            .Add(x => x.AutomaticCells, (IReadOnlyList<AutomaticCell>)[]));

        Assert.NotEmpty(cut.FindAll(".wms-automatic-cells"));
        Assert.Empty(cut.FindAll(".wms-automatic-cells li"));
    }

    [Fact]
    public void AutomaticCellsAreListedWithTheirRelocator()
    {
        var relocator = Guid.NewGuid();
        var automaticCell = new AutomaticCell(3m, ItemId, relocator);
        var cut = Render<LinkedCells>(p => p
            .Add(x => x.ItemId, ItemId)
            .Add(x => x.Cells, [])
            .Add(x => x.AutomaticCells, (IReadOnlyList<AutomaticCell>)[automaticCell]));

        var item = cut.Find(".wms-automatic-cells li[data-cell-id='3']");
        Assert.Contains(relocator.ToString(), item.TextContent);
    }
}
