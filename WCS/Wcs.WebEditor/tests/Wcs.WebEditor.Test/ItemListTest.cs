using Bunit;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Lists;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// <see cref="ItemList"/> da sola (compito 10): ordina, filtra per testo, invoca <c>OnSelect</c> al
/// clic su una riga. Il cablaggio con <c>PlantCanvas</c> (la selezione che evidenzia davvero il nodo
/// sulla tela) e' provato a parte in <c>PlantCanvasListsTest</c>.
/// </summary>
public sealed class ItemListTest : PlantCanvasBunitContext
{
    private static PlantItemDto Item(string? name) => new(
        Guid.NewGuid(), name, DeviceTypes.Automation, null, null, null, default, default, 0, null, 0, 0, 0, 0,
        default!, X: 0, Y: 0, Width: 100, Height: 100, Rotation: 0);

    [Fact]
    public void RendersOneRowPerItemOrderedByName()
    {
        var b = Item("Beta");
        var a = Item("Alfa");
        var cut = Render<ItemList>(parameters => parameters.Add(p => p.Items, [b, a]));

        var rows = cut.FindAll("tr.item-list-row");
        Assert.Equal(2, rows.Count);
        Assert.Equal("Alfa", rows[0].QuerySelector("td")!.TextContent);
        Assert.Equal("Beta", rows[1].QuerySelector("td")!.TextContent);
    }

    [Fact]
    public void AnItemWithoutANameIsShownByItsRawId()
    {
        var item = Item(null);
        var cut = Render<ItemList>(parameters => parameters.Add(p => p.Items, [item]));

        Assert.Contains(item.ItemId.ToString(), cut.Find("tr.item-list-row td").TextContent);
    }

    [Fact]
    public void TypingInTheFilterKeepsOnlyMatchingRowsByName()
    {
        var a = Item("Nastro 1");
        var b = Item("Elevatore 1");
        var cut = Render<ItemList>(parameters => parameters.Add(p => p.Items, [a, b]));

        cut.Find("#item-list-filter").Input("nastro");

        var rows = cut.FindAll("tr.item-list-row");
        Assert.Single(rows);
        Assert.Contains("Nastro 1", rows[0].TextContent);
    }

    /// <summary>Un dispositivo senza nome resterebbe irraggiungibile dal filtro se questo cercasse solo nel nome: deve trovarlo anche per identificativo.</summary>
    [Fact]
    public void TypingAFragmentOfTheIdFindsAnUnnamedItem()
    {
        var unnamed = Item(null);
        var named = Item("Nastro 1");
        var cut = Render<ItemList>(parameters => parameters.Add(p => p.Items, [unnamed, named]));

        cut.Find("#item-list-filter").Input(unnamed.ItemId.ToString()[..8]);

        var rows = cut.FindAll("tr.item-list-row");
        Assert.Single(rows);
        Assert.Contains(unnamed.ItemId.ToString(), rows[0].TextContent);
    }

    [Fact]
    public void ClickingARowRaisesOnSelectWithThatItemId()
    {
        var item = Item("Nastro 1");
        Guid? selected = null;
        var cut = Render<ItemList>(parameters => parameters
            .Add(p => p.Items, [item])
            .Add(p => p.OnSelect, id => selected = id));

        cut.Find("tr.item-list-row").Click();

        Assert.Equal(item.ItemId, selected);
    }

    [Fact]
    public void TheSelectedRowIsMarkedDistinctlyFromTheOthers()
    {
        var a = Item("Alfa");
        var b = Item("Beta");
        var cut = Render<ItemList>(parameters => parameters
            .Add(p => p.Items, [a, b])
            .Add(p => p.SelectedItemId, a.ItemId));

        var rows = cut.FindAll("tr.item-list-row");
        var selectedRow = rows.Single(r => r.GetAttribute("data-item-id") == a.ItemId.ToString());
        var otherRow = rows.Single(r => r.GetAttribute("data-item-id") == b.ItemId.ToString());

        Assert.Equal("true", selectedRow.GetAttribute("aria-selected"));
        Assert.Equal("false", otherRow.GetAttribute("aria-selected"));
        // bUnit non applica il foglio di stile della libreria: il segnale visivo e' un attributo di
        // presentazione in linea, non una classe che da sola non proverebbe niente in un test (come
        // PlantCanvas.StrokeWidthOf).
        Assert.Contains("background-color", selectedRow.GetAttribute("style"));
    }
}
