using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Drawio.Components;
using Wcs.WebEditor.Drawio.Coordination;
using Wcs.WebEditor.Properties;
using Wcs.WebEditor.Wms;
using Xunit;

namespace Wcs.WebEditor.Test;

public sealed class DrawioPropertyDockTest : PlantCanvasBunitContext
{
    [Fact]
    public void TheDockExposesAnAccessibleKeyboardSeparator()
    {
        var cut = Render<DrawioPropertyDock>(p => p.Add(x => x.State, EmptyState()));
        var separator = cut.Find("#drawio-property-dock-resizer");
        Assert.Equal("separator", separator.GetAttribute("role"));
        Assert.Equal("vertical", separator.GetAttribute("aria-orientation"));
        Assert.Equal("0", separator.GetAttribute("tabindex"));
        Assert.Equal("320", separator.GetAttribute("aria-valuemin"));
        Assert.Equal("560", separator.GetAttribute("aria-valuemax"));
        Assert.Equal("320", separator.GetAttribute("aria-valuenow"));
        Assert.Equal("Resize properties panel", separator.GetAttribute("aria-label"));
    }

    [Fact]
    public async Task TheResizeModuleAttachesOnceAndDetachesTheSameElementOnDisposal()
    {
        var module = JSInterop.SetupModule(DrawioPropertyDock.ModulePath);
        module.SetupVoid("attach", _ => true).SetVoidResult();
        module.SetupVoid("detach", _ => true).SetVoidResult();
        var cut = Render<DrawioPropertyDock>(p => p.Add(x => x.State, EmptyState()));
        cut.Render();

        var element = Assert.IsType<ElementReference>(Assert.Single(Assert.Single(module.Invocations["attach"]).Arguments));
        await Assert.IsAssignableFrom<IAsyncDisposable>(cut.Instance).DisposeAsync();
        Assert.Equal(element, Assert.Single(Assert.Single(module.Invocations["detach"]).Arguments));
    }

    [Fact]
    public void NoSelectionKeepsTheStableHintAndDockIds()
    {
        var cut = Render<DrawioPropertyDock>(p => p.Add(x => x.State, EmptyState()));

        Assert.Single(cut.FindAll("#drawio-property-dock"));
        Assert.Single(cut.FindAll("#drawio-property-hint"));
        Assert.Empty(cut.FindAll("#drawio-property-panel"));
    }

    [Fact]
    public async Task OrphanSelectionForwardsTypingAndConfirmationOnce()
    {
        var typed = "";
        var confirmed = 0;
        var state = EmptyState() with
        {
            ReassociableCell = "orphan",
            ReassociationCandidatesLine = "No candidates",
        };
        var cut = Render<DrawioPropertyDock>(p => p
            .Add(x => x.State, state)
            .Add(x => x.OnReassociateTypedChanged,
                EventCallback.Factory.Create<string>(this, value => typed = value))
            .Add(x => x.OnReassociate,
                EventCallback.Factory.Create(this, () => confirmed++)));

        await cut.Find("#drawio-reassociate-id").InputAsync("device-id");
        await cut.Find("#drawio-reassociate-confirm").ClickAsync(new());

        Assert.Equal("device-id", typed);
        Assert.Equal(1, confirmed);
        Assert.Single(cut.FindAll("#drawio-reassociate-none"));
    }

    [Fact]
    public void LoadingReadAndWriteFailuresKeepTheirExistingElements()
    {
        var itemId = Guid.NewGuid();
        var loading = Render<DrawioPropertyDock>(p => p.Add(x => x.State,
            EmptyState() with { ConfigItemId = itemId, ConfigSubjectName = "Device" }));
        Assert.Single(loading.FindAll("#drawio-property-loading"));

        var configured = Render<DrawioPropertyDock>(p => p.Add(x => x.State,
            EmptyState() with
            {
                ConfigItemId = itemId,
                ConfigSubjectName = "Device",
                Config = new PlantItemConfigDto { ItemId = itemId },
            }));
        Assert.Single(configured.FindComponents<PropertyPanel>());
        Assert.Empty(configured.FindAll("#drawio-cells, #drawio-mission-steps"));

        var failed = Render<DrawioPropertyDock>(p => p.Add(x => x.State,
            EmptyState() with
            {
                ConfigItemId = itemId,
                ConfigSubjectName = "Device",
                ConfigError = "read failed",
                ConfigWriteError = "write failed",
                ReassociateOutcome = "association failed",
            }));

        Assert.Equal("read failed", failed.Find("#drawio-property-error").TextContent);
        Assert.Equal("write failed", failed.Find("#drawio-property-write-error").TextContent);
        Assert.Empty(failed.FindAll("#drawio-cells-write-error"));
        Assert.Equal("association failed", failed.Find("#drawio-reassociate-outcome").TextContent);
    }

    [Fact]
    public async Task TheFooterExplainsThePropertyTheOperatorIsOnAndResetsForAnotherDevice()
    {
        var firstId = Guid.NewGuid();
        var first = EmptyState() with
        {
            ConfigItemId = firstId,
            ConfigSubjectName = "First",
            Config = new PlantItemConfigDto { ItemId = firstId, Name = "First" },
        };
        var cut = Render<DrawioPropertyDock>(p => p.Add(x => x.State, first));

        Assert.Equal("Select a property to see what it controls.",
            cut.Find("#drawio-property-description-body").TextContent.Trim());

        await cut.Find("[data-property='Name']").TriggerEventAsync("onfocusin", new FocusEventArgs());

        var name = PropertyModel.BuildGroups().SelectMany(group => group.Fields)
            .Single(field => field.PropertyName == nameof(PlantItemConfigDto.Name));
        Assert.Equal(name.Label, cut.Find("#drawio-property-description-title").TextContent.Trim());
        Assert.Equal(name.Help, cut.Find("#drawio-property-description-body").TextContent.Trim());

        var secondId = Guid.NewGuid();
        cut.Render(p => p.Add(x => x.State, first with
        {
            ConfigItemId = secondId,
            ConfigSubjectName = "Second",
            Config = new PlantItemConfigDto { ItemId = secondId, Name = "Second" },
        }));

        Assert.Empty(cut.FindAll("#drawio-property-description-title"));
        Assert.Equal("Select a property to see what it controls.",
            cut.Find("#drawio-property-description-body").TextContent.Trim());
    }

    [Fact]
    public async Task StorageConfigurationAndItsCellWindowForwardTheirOwnCommandsOnce()
    {
        var itemId = Guid.NewGuid();
        var config = new PlantItemConfigDto { ItemId = itemId, Name = "Storage" };
        var cell = new WmsCell(101m, "WH", "AREA", 1, 1, "A", "B", "cell");
        var mapping = new DoorMappingDto(1, itemId, "A", 101m, "A", 0, false);
        var configCalls = 0;
        var saveCalls = 0;
        var clearCalls = 0;
        var dock = Render<DrawioPropertyDock>(p => p
            .Add(x => x.State, EmptyState() with
            {
                ConfigItemId = itemId,
                Config = config,
                ConfigSubjectName = "Storage",
                CellsKind = CellPanelKind.Doors,
                WmsCells = [cell],
                CellsMappings = [mapping],
            })
            .Add(x => x.OnSaveConfig,
                EventCallback.Factory.Create<PlantItemConfigDto>(this, _ => configCalls++)));
        var cells = Render<DrawioLinkedStorageCells>(p => p
            .Add(x => x.ItemId, itemId)
            .Add(x => x.State, EmptyState() with
            {
                ConfigItemId = itemId,
                Config = config,
                ConfigSubjectName = "Storage",
                CellsKind = CellPanelKind.Doors,
                WmsCells = [cell],
                CellsMappings = [mapping],
            })
            .Add(x => x.OnSaveDoorMappings,
                EventCallback.Factory.Create<IReadOnlyList<DoorMappingDto>>(this, _ => saveCalls++))
            .Add(x => x.OnClearDoorMappings,
                EventCallback.Factory.Create(this, () => clearCalls++)));

        await dock.FindComponent<PropertyPanel>().Instance.OnSave.InvokeAsync(config);
        await cells.FindComponent<DoorMappings>().Instance.OnSave.InvokeAsync([mapping]);
        await cells.FindComponent<DoorMappings>().Instance.OnClear.InvokeAsync();

        Assert.Equal(1, configCalls);
        Assert.Equal(1, saveCalls);
        Assert.Equal(1, clearCalls);
        Assert.Contains("1 door mapped", cells.Find("#drawio-cells-summary").TextContent);
        Assert.Empty(dock.FindAll("#drawio-cells"));
    }

    [Fact]
    public async Task AutomationCellWindowForwardsLinkAndUnlinkOnce()
    {
        var itemId = Guid.NewGuid();
        var config = new PlantItemConfigDto { ItemId = itemId, Name = "Automation" };
        var cell = new WmsCell(202m, "WH", "AREA", 1, 1, null, null, "cell");
        var linkCalls = 0;
        var unlinkCalls = 0;
        var cut = Render<DrawioLinkedStorageCells>(p => p
            .Add(x => x.ItemId, itemId)
            .Add(x => x.State, EmptyState() with
            {
                ConfigItemId = itemId,
                Config = config,
                ConfigSubjectName = "Automation",
                CellsKind = CellPanelKind.LinkedCell,
                WmsCells = [cell],
                CellsMappings = [],
            })
            .Add(x => x.OnLinkCell,
                EventCallback.Factory.Create<WmsCell>(this, _ => linkCalls++))
            .Add(x => x.OnUnlinkCell,
                EventCallback.Factory.Create(this, () => unlinkCalls++)));

        await cut.FindComponent<LinkedCells>().Instance.OnLink.InvokeAsync(cell);
        await cut.FindComponent<LinkedCells>().Instance.OnUnlink.InvokeAsync();

        Assert.Equal(1, linkCalls);
        Assert.Equal(1, unlinkCalls);
        Assert.Contains("none connected", cut.Find("#drawio-cells-summary").TextContent);
    }

    [Fact]
    public async Task TheCellWindowFiltersCandidatesWithoutChangingTheCurrentLink()
    {
        var itemId = Guid.NewGuid();
        var first = new WmsCell(201m, "WH-A", "AREA", 1, 1, "A-01", null, "first");
        var second = new WmsCell(202m, "WH-B", "AREA", 2, 3, "B-02", null, "second");
        var cut = Render<DrawioLinkedStorageCells>(p => p
            .Add(x => x.ItemId, itemId)
            .Add(x => x.State, EmptyState() with
            {
                ConfigItemId = itemId,
                Config = new PlantItemConfigDto { ItemId = itemId, LinkedCellId = first.CellId },
                CellsKind = CellPanelKind.LinkedCell,
                WmsCells = [first, second],
                CellsMappings = [],
            }));

        await cut.Find("#drawio-linked-storage-cells-filter").InputAsync("WH-B");

        var row = Assert.Single(cut.FindAll("#drawio-cells-details tr[data-cell-id]"));
        Assert.Equal(second.CellId.ToString(), row.GetAttribute("data-cell-id"));
        Assert.Contains($"cell {first.CellId} connected", cut.Find("#drawio-cells-summary").TextContent);

        await cut.Find("#drawio-linked-storage-cells-filter").InputAsync("missing");

        Assert.Empty(cut.FindAll("#drawio-cells-details tr[data-cell-id]"));
        Assert.Equal("No warehouse cell matches this filter.", cut.Find("#drawio-cells-no-match").TextContent);
        Assert.Contains($"cell {first.CellId} connected", cut.Find("#drawio-cells-summary").TextContent);
    }

    [Fact]
    public async Task FilteringDoorRowsDoesNotDropHiddenMappingsWhenSaving()
    {
        var itemId = Guid.NewGuid();
        var first = new WmsCell(201m, "WH-A", "AREA", 1, 1, "A-01", null, "first");
        var second = new WmsCell(202m, "WH-B", "AREA", 2, 2, null, "B-02", "second");
        var current = new[]
        {
            new DoorMappingDto(1, itemId, first.DoorA, first.CellId, "A", 0, false),
            new DoorMappingDto(2, itemId, second.DoorB, second.CellId, "B", 0, false),
        };
        IReadOnlyList<DoorMappingDto>? saved = null;
        var cut = Render<DrawioLinkedStorageCells>(p => p
            .Add(x => x.ItemId, itemId)
            .Add(x => x.State, EmptyState() with
            {
                ConfigItemId = itemId,
                Config = new PlantItemConfigDto { ItemId = itemId },
                CellsKind = CellPanelKind.Doors,
                WmsCells = [first, second],
                CellsMappings = current,
            })
            .Add(x => x.OnSaveDoorMappings,
                EventCallback.Factory.Create<IReadOnlyList<DoorMappingDto>>(this, mappings => saved = mappings)));

        await cut.Find("#drawio-linked-storage-cells-filter").InputAsync("WH-A");
        Assert.Single(cut.FindAll("#drawio-cells-details tr[data-cell-id]"));
        cut.Find("#door-mappings-save-button").Click();

        Assert.NotNull(saved);
        Assert.Equal([first.CellId, second.CellId], saved.Select(mapping => mapping.CellId).Order().ToArray());

        await cut.Find("#drawio-linked-storage-cells-filter").InputAsync("");
        Assert.True(cut.Find($"#door-a-{first.CellId}").HasAttribute("checked"));
        Assert.True(cut.Find($"#door-b-{second.CellId}").HasAttribute("checked"));
    }

    private static DrawioSelectionState EmptyState() => new(
        SelectedCellIds: null,
        SelectionUnavailable: false,
        SelectionLine: "Nothing selected",
        ConfigItemId: null,
        Config: null,
        ConfigError: null,
        ConfigSubjectName: "",
        ConfigWriteError: null,
        ConfigWritten: null,
        CellsKind: CellPanelKind.None,
        CellsMappings: null,
        WmsCells: null,
        AutomaticCells: null,
        CellsError: null,
        CellsErrorDetail: null,
        CellsNotice: null,
        CellsWriteError: null,
        CellsWritten: null,
        ReassociableCell: null,
        ReassociateTyped: "",
        ReassociateOutcome: null,
        ReassociateWentThrough: false,
        ReassociationCandidates: null,
        ReassociationCandidatesLine: "No candidates",
        ReassociateRound: 0,
        Generation: 0);
}
