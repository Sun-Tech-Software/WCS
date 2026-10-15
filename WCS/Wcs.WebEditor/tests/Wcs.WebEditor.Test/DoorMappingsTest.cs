using Bunit;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Wms;
using Xunit;

namespace Wcs.WebEditor.Test;

/// <summary>
/// <c>DoorMappings</c> da solo (compito 11): come <c>PropertyPanelTest</c> per <c>PropertyPanel</c> -
/// non tocca <c>PlantCanvas</c> ne' un host. Il cablaggio (permesso, pila dell'annulla, PUT/DELETE,
/// messaggio 409/423) e' provato a parte in <c>PlantCanvasCellsPanelTest</c>.
/// </summary>
public sealed class DoorMappingsTest : PlantCanvasBunitContext
{
    private static readonly Guid ItemId = Guid.NewGuid();

    private static WmsCell CellWithBothDoors(decimal id) => new(id, "W1", "A", 1, 1, $"A{id}", $"B{id}", "descrizione");
    private static WmsCell CellWithOnlyDoorA(decimal id) => new(id, "W1", "A", 1, 1, $"A{id}", null, "descrizione");

    [Fact]
    public void WithNoCellsNothingIsRenderedExceptThePlaceholder()
    {
        var cut = Render<DoorMappings>(p => p
            .Add(x => x.ItemId, ItemId)
            .Add(x => x.Cells, [])
            .Add(x => x.CurrentMappings, []));

        Assert.NotEmpty(cut.FindAll(".wms-cells-empty"));
        Assert.Empty(cut.FindAll("table"));
    }

    /// <summary>
    /// Cells whose doors are all missing leave both columns empty: with no filter typed nothing was
    /// filtered out, and the panel says that no cell has a door - not that none matches a filter.
    /// </summary>
    [Fact]
    public void CellsWithoutDoorsAndNoFilterSayNoCellHasADoorNotThatNoneMatches()
    {
        var doorless = new WmsCell(1, "W1", "A", 1, 1, null, null, "descrizione");
        var cut = Render<DoorMappings>(p => p
            .Add(x => x.ItemId, ItemId)
            .Add(x => x.Cells, [doorless])
            .Add(x => x.CurrentMappings, []));

        Assert.Equal("No cell of the warehouse system has a door to map.", cut.Find("#drawio-cells-no-door").TextContent);
        Assert.Empty(cut.FindAll("#drawio-cells-no-match"));

        cut.Render(p => p.Add(x => x.Filter, "zzz"));
        Assert.Equal("No warehouse cell matches this filter.", cut.Find("#drawio-cells-no-match").TextContent);
        Assert.Empty(cut.FindAll("#drawio-cells-no-door"));
    }

    /// <summary>Una cella senza porta B non compare nella colonna B (ne' viceversa): senza una porta non c'e' codice da mandare a ReplaceDoorMappingsAsync per quel lato.</summary>
    [Fact]
    public void ACellWithoutOneDoorIsOmittedFromThatColumnOnly()
    {
        var cut = Render<DoorMappings>(p => p
            .Add(x => x.ItemId, ItemId)
            .Add(x => x.Cells, [CellWithOnlyDoorA(1)])
            .Add(x => x.CurrentMappings, []));

        Assert.NotEmpty(cut.FindAll("fieldset[data-side='A'] tr[data-cell-id='1']"));
        Assert.Empty(cut.FindAll("fieldset[data-side='B'] tr[data-cell-id='1']"));
    }

    /// <summary>Precompila la selezione dalle mappature esistenti (come SelectDoorCode dell'Editor WinForms): il confronto e' sul codice porta, non sul campo Side della riga.</summary>
    [Fact]
    public void ExistingMappingsPrecheckTheMatchingCellsOnTheRightSide()
    {
        var cell = CellWithBothDoors(1);
        var existing = new DoorMappingDto(0, ItemId, cell.DoorA, cell.CellId, "A", 0, false);
        var cut = Render<DoorMappings>(p => p
            .Add(x => x.ItemId, ItemId)
            .Add(x => x.Cells, [cell])
            .Add(x => x.CurrentMappings, [existing]));

        Assert.True(cut.Find("#door-a-1").HasAttribute("checked"));
        Assert.False(cut.Find("#door-b-1").HasAttribute("checked"));
    }

    [Fact]
    public void SaveIsDisabledWithoutEditPermission()
    {
        var cut = Render<DoorMappings>(p => p
            .Add(x => x.ItemId, ItemId)
            .Add(x => x.Cells, [CellWithBothDoors(1)])
            .Add(x => x.CurrentMappings, [])
            .Add(x => x.CanEdit, false));

        Assert.True(cut.Find("#door-mappings-save-button").HasAttribute("disabled"));
    }

    /// <summary>Il gesto di "Save" (compito 11): ricostruisce l'INTERO insieme dalle due tabelle, non solo la riga appena toccata - la riga preesistente su B resta insieme a quella nuova su A.</summary>
    [Fact]
    public void SavingChecksInBothColumnsEmitsTheWholeDesiredSetNotJustTheNewRow()
    {
        var cellA = CellWithBothDoors(1);
        var cellB = CellWithBothDoors(2);
        var existingOnB = new DoorMappingDto(0, ItemId, cellB.DoorB, cellB.CellId, "B", 0, false);
        IReadOnlyList<DoorMappingDto>? saved = null;
        var cut = Render<DoorMappings>(p => p
            .Add(x => x.ItemId, ItemId)
            .Add(x => x.Cells, [cellA, cellB])
            .Add(x => x.CurrentMappings, [existingOnB])
            .Add(x => x.CanEdit, true)
            .Add(x => x.OnSave, (IReadOnlyList<DoorMappingDto> m) => saved = m));

        cut.Find("#door-a-1").Change(true);
        cut.Find("#door-mappings-save-button").Click();

        Assert.NotNull(saved);
        Assert.Equal(2, saved!.Count);
        Assert.Contains(saved, m => m.CellId == 1 && m.Side == "A" && m.DoorCode == cellA.DoorA);
        Assert.Contains(saved, m => m.CellId == 2 && m.Side == "B" && m.DoorCode == cellB.DoorB);
    }

    [Fact]
    public void ClearButtonIsDisabledWithNoSelectionAndInvokesOnClearWhenClicked()
    {
        var cell = CellWithBothDoors(1);
        var existing = new DoorMappingDto(0, ItemId, cell.DoorA, cell.CellId, "A", 0, false);
        var cleared = false;
        var cut = Render<DoorMappings>(p => p
            .Add(x => x.ItemId, ItemId)
            .Add(x => x.Cells, [cell])
            .Add(x => x.CurrentMappings, [existing])
            .Add(x => x.CanEdit, true)
            .Add(x => x.OnClear, () => cleared = true));

        Assert.False(cut.Find("#door-mappings-clear-button").HasAttribute("disabled"));
        cut.Find("#door-mappings-clear-button").Click();

        Assert.True(cleared);
    }
}
