using Bunit;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Lists;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// <see cref="JunctionList"/> da sola (compito 10): come <c>ItemList</c>, ordina, filtra per testo,
/// invoca <c>OnSelect</c> al clic su una riga - qui risolvendo i nomi di origine e destinazione dai
/// dispositivi dell'impianto. Il cablaggio con <c>PlantCanvas</c> e' provato a parte in
/// <c>PlantCanvasListsTest</c>.
/// </summary>
public sealed class JunctionListTest : PlantCanvasBunitContext
{
    private static PlantItemDto Item(string? name) => new(
        Guid.NewGuid(), name, DeviceTypes.Automation, null, null, null, default, default, 0, null, 0, 0, 0, 0,
        default!, X: 0, Y: 0, Width: 100, Height: 100, Rotation: 0);

    [Fact]
    public void RendersOneRowPerJunctionWithSourceAndDestinationNamesOrderedByPriority()
    {
        var a = Item("Nastro 1");
        var b = Item("Elevatore 1");
        var high = new JunctionDto(Guid.NewGuid(), a.ItemId, b.ItemId, 5);
        var low = new JunctionDto(Guid.NewGuid(), b.ItemId, a.ItemId, 1);
        var cut = Render<JunctionList>(parameters => parameters
            .Add(p => p.Junctions, [high, low])
            .Add(p => p.Items, [a, b]));

        var rows = cut.FindAll("tr.junction-list-row");
        Assert.Equal(2, rows.Count);
        Assert.Contains("Elevatore 1", rows[0].TextContent); // priorita' 1: prima riga
        Assert.Contains("Nastro 1", rows[1].TextContent); // priorita' 5: seconda riga
    }

    /// <summary>Una giunzione scucita (l'estremo non e' fra i dispositivi dell'impianto) mostra il Guid nudo, invece di sollevare un'eccezione o sparire dall'elenco.</summary>
    [Fact]
    public void ADanglingEndpointIsShownByItsRawId()
    {
        var a = Item("Nastro 1");
        var missingDestination = Guid.NewGuid();
        var junction = new JunctionDto(Guid.NewGuid(), a.ItemId, missingDestination, 0);
        var cut = Render<JunctionList>(parameters => parameters
            .Add(p => p.Junctions, [junction])
            .Add(p => p.Items, [a]));

        Assert.Contains(missingDestination.ToString(), cut.Find("tr.junction-list-row").TextContent);
    }

    [Fact]
    public void TypingInTheFilterKeepsOnlyJunctionsTouchingAMatchingDevice()
    {
        var a = Item("Nastro 1");
        var b = Item("Elevatore 1");
        var c = Item("Nastro 2");
        var matching = new JunctionDto(Guid.NewGuid(), a.ItemId, b.ItemId, 0);
        var other = new JunctionDto(Guid.NewGuid(), b.ItemId, c.ItemId, 0);
        var cut = Render<JunctionList>(parameters => parameters
            .Add(p => p.Junctions, [matching, other])
            .Add(p => p.Items, [a, b, c]));

        cut.Find("#junction-list-filter").Input("Nastro 1");

        var rows = cut.FindAll("tr.junction-list-row");
        Assert.Single(rows);
        Assert.Contains("Nastro 1", rows[0].TextContent);
    }

    [Fact]
    public void ClickingARowRaisesOnSelectWithThatJunctionId()
    {
        var a = Item("Nastro 1");
        var b = Item("Elevatore 1");
        var junction = new JunctionDto(Guid.NewGuid(), a.ItemId, b.ItemId, 0);
        Guid? selected = null;
        var cut = Render<JunctionList>(parameters => parameters
            .Add(p => p.Junctions, [junction])
            .Add(p => p.Items, [a, b])
            .Add(p => p.OnSelect, id => selected = id));

        cut.Find("tr.junction-list-row").Click();

        Assert.Equal(junction.JunctionId, selected);
    }

    [Fact]
    public void TheSelectedRowIsMarkedDistinctlyFromTheOthers()
    {
        var a = Item("Nastro 1");
        var b = Item("Elevatore 1");
        var one = new JunctionDto(Guid.NewGuid(), a.ItemId, b.ItemId, 0);
        var two = new JunctionDto(Guid.NewGuid(), b.ItemId, a.ItemId, 1);
        var cut = Render<JunctionList>(parameters => parameters
            .Add(p => p.Junctions, [one, two])
            .Add(p => p.Items, [a, b])
            .Add(p => p.SelectedJunctionId, one.JunctionId));

        var rows = cut.FindAll("tr.junction-list-row");
        var selectedRow = rows.Single(r => r.GetAttribute("data-junction-id") == one.JunctionId.ToString());
        var otherRow = rows.Single(r => r.GetAttribute("data-junction-id") == two.JunctionId.ToString());

        Assert.Equal("true", selectedRow.GetAttribute("aria-selected"));
        Assert.Equal("false", otherRow.GetAttribute("aria-selected"));
        // bUnit non applica il foglio di stile della libreria: il segnale visivo e' un attributo di
        // presentazione in linea, non una classe che da sola non proverebbe niente in un test (come
        // ItemListTest, e come PlantCanvas.StrokeWidthOf per la tela).
        Assert.Contains("background-color", selectedRow.GetAttribute("style"));
        Assert.DoesNotContain("background-color", otherRow.GetAttribute("style"));
    }
}
