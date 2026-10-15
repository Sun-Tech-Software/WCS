using Bunit;
using System.Globalization;
using System.Reflection;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Model;
using Wcs.WebEditor.Properties;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// <see cref="PropertyPanel"/> da solo (compito 9): come <c>PaletteTest</c> per <c>Palette</c>, non
/// tocca <c>PlantCanvas</c> - il cablaggio (permesso, pila dell'annulla, messaggio 409/423) e' provato
/// a parte in <c>PlantCanvasConfigurePanelTest</c>. Non tocca nemmeno un host: <c>Config</c> e
/// <c>Items</c> sono dati in ingresso, <c>OnSave</c> l'unica uscita osservata.
/// </summary>
public sealed class PropertyPanelTest : PlantCanvasBunitContext
{
    private static PlantItemConfigDto NewConfig(Guid id) => new()
    {
        ItemId = id,
        Name = "dispositivo di prova",
        DeviceType = (short)DeviceTypes.Automation,
        TimeOutPlcMoving = 1000,
        RelocateItemId = null,
    };

    // bUnit wraps its AngleSharp elements and does not model native keyboard focus. Inspect the
    // generation used as the input's Blazor @key to detect replacement without wrapper identity.
    private static int InputGeneration(PropertyPanel panel, string property) =>
        ((Dictionary<string, int>)typeof(PropertyPanel)
            .GetField("_numericInputGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(panel)!).GetValueOrDefault(property);

    [Fact]
    public void WithNoConfigNothingIsRenderedExceptThePlaceholder()
    {
        var cut = Render<PropertyPanel>(p => p.Add(x => x.Config, null));

        Assert.NotEmpty(cut.FindAll(".property-panel-empty"));
        Assert.Empty(cut.FindAll("fieldset"));
        Assert.Empty(cut.FindAll("#property-panel-save-button"));
    }

    /// <summary>
    /// Le sette categorie del contratto (misurate da <see cref="PropertyModel"/>, non da un letterale
    /// qui): un gruppo per categoria, non una griglia piatta di sessanta campi.
    /// </summary>
    [Fact]
    public void RendersOneFieldsetPerCategoryFoundByPropertyModel()
    {
        var cut = Render<PropertyPanel>(p => p
            .Add(x => x.Config, NewConfig(Guid.NewGuid()))
            .Add(x => x.CanEdit, true));

        var expectedCategories = PropertyModel.BuildGroups().Select(g => g.Category).ToList();
        var renderedCategories = cut.FindAll("fieldset.property-panel-group")
            .Select(f => f.GetAttribute("data-category") ?? "").ToList();

        Assert.Equal(expectedCategories, renderedCategories);
    }

    /// <summary>
    /// A property's name may break between the words of its camel case, and there only (passata
    /// grafica, final fix round): the draw.io page's column gives the names 9rem, and the longest are
    /// twice as long - without these places they broke at whatever letter met the edge. The name reads
    /// the same: the places to break (<c>&lt;wbr&gt;</c>) are not text.
    /// </summary>
    [Fact]
    public void ALongNameMayBreakBetweenTheWordsOfItsCamelCase()
    {
        var cut = Render<PropertyPanel>(p => p
            .Add(x => x.Config, NewConfig(Guid.NewGuid()))
            .Add(x => x.CanEdit, true));

        var label = cut.Find("[data-property='AcceptLoadUnitIfDestinationIsAvailable'] label");
        Assert.Equal("AcceptLUIfDestinationIsAvailable", label.TextContent);
        Assert.Equal("Accept<wbr>LU<wbr>If<wbr>Destination<wbr>Is<wbr>Available", label.InnerHtml);
    }

    [Theory]
    [InlineData("Name", "Name")]
    [InlineData("TriggerItemToExtractPoint1Item", "Trigger<wbr>Item<wbr>To<wbr>Extract<wbr>Point1<wbr>Item")]
    [InlineData("RejectItemDestination_GetLUInfo", "Reject<wbr>Item<wbr>Destination_<wbr>Get<wbr>LU<wbr>Info")]
    [InlineData("LoadUnit&Cell", "Load<wbr>Unit&amp;Cell")]
    public void TheWordsOfANameAreFoundAtItsCapitalsAndAfterAnUnderscore(string name, string markup) =>
        Assert.Equal(markup, PropertyPanel.WordBreaks(name));

    /// <summary>ItemId (l'unica proprieta' [ReadOnly(true)]): un testo, non una casella modificabile.</summary>
    [Fact]
    public void TheReadOnlyItemIdFieldHasNoInputElement()
    {
        var id = Guid.NewGuid();
        var cut = Render<PropertyPanel>(p => p
            .Add(x => x.Config, NewConfig(id))
            .Add(x => x.CanEdit, true));

        var field = cut.Find("div[data-property='ItemId']");
        Assert.Empty(field.QuerySelectorAll("input, select"));
        Assert.Contains(id.ToString(), field.TextContent);
    }

    /// <summary>DeviceType (uno dei cinque con EnumCodeConverter&lt;&gt;): una casella a elenco con il testo del convertitore, non la cifra nuda.</summary>
    [Fact]
    public void AnEnumCodedFieldRendersAsASelectWithConverterTextNotRawNumbers()
    {
        var cut = Render<PropertyPanel>(p => p
            .Add(x => x.Config, NewConfig(Guid.NewGuid()))
            .Add(x => x.CanEdit, true));

        var select = cut.Find("div[data-property='DeviceType'] select");
        var optionTexts = select.QuerySelectorAll("option").Select(o => o.TextContent).ToList();

        Assert.Contains("Automation", optionTexts);
        Assert.Contains("Storage", optionTexts);
        Assert.DoesNotContain(optionTexts, t => t.Trim() == "3"); // non la cifra nuda di Storage

        // Il valore corrente (Automation = 0) e' quello selezionato.
        Assert.Equal("0", select.GetAttribute("value"));
    }

    /// <summary>
    /// RelocateItemId (una delle quattro proprieta' arricchite fuori dal contratto, si veda il
    /// remarks di PlantItemConfigDto): un elenco di dispositivi con una scelta vuota per "nessuno",
    /// non una casella di testo dove scrivere un Guid a mano.
    /// </summary>
    [Fact]
    public void AnItemReferenceFieldRendersAsASelectOfDevicesWithANoneOption()
    {
        var deviceId = Guid.NewGuid();
        var device = new PlantItemDto(deviceId, "Ricollocatore", DeviceTypes.Storage, null, null, null,
            default, default, 0, null, 0, 0, 0, 0, default!);

        var cut = Render<PropertyPanel>(p => p
            .Add(x => x.Config, NewConfig(Guid.NewGuid()))
            .Add(x => x.Items, [device])
            .Add(x => x.CanEdit, true));

        var select = cut.Find("div[data-property='RelocateItemId'] select");
        var options = select.QuerySelectorAll("option").ToList();

        Assert.Contains(options, o => o.GetAttribute("value") == "" && o.TextContent.Contains("(none)"));
        Assert.Contains(options, o => o.GetAttribute("value") == deviceId.ToString() && o.TextContent.Contains("Ricollocatore"));
        // Nessuna configurazione: la scelta vuota e' quella selezionata, non un Guid inventato.
        Assert.Equal("", select.GetAttribute("value"));
    }

    [Fact]
    public void WithoutEditPermissionTheSaveButtonIsDisabledButFieldsAreStillReadable()
    {
        var cut = Render<PropertyPanel>(p => p
            .Add(x => x.Config, NewConfig(Guid.NewGuid()))
            .Add(x => x.CanEdit, false));

        Assert.True(cut.Find("#property-panel-save-button").HasAttribute("disabled"));
        // I campi restano consultabili (come il campo priorita' di una giunzione, compito 8): lo
        // stesso valore di prima si legge, solo non si puo' salvare.
        Assert.Equal("dispositivo di prova", cut.Find("div[data-property='Name'] input").GetAttribute("value"));
    }

    [Fact]
    public void EditingATextFieldThenSavingInvokesOnSaveWithTheEditedValueAndTheRestUnchanged()
    {
        PlantItemConfigDto? saved = null;
        var id = Guid.NewGuid();
        var cut = Render<PropertyPanel>(p => p
            .Add(x => x.Config, NewConfig(id))
            .Add(x => x.CanEdit, true)
            .Add(x => x.OnSave, config => saved = config));

        cut.Find("div[data-property='Name'] input").Change("nome nuovo");
        cut.Find("#property-panel-save-button").Click();

        Assert.NotNull(saved);
        Assert.Equal(id, saved!.ItemId);
        Assert.Equal("nome nuovo", saved.Name);
        Assert.Equal(1000, saved.TimeOutPlcMoving); // non toccato: resta quello di Config
    }

    [Fact]
    public void ChoosingAnEnumOptionThenSavingWritesItsNumericCode()
    {
        PlantItemConfigDto? saved = null;
        var cut = Render<PropertyPanel>(p => p
            .Add(x => x.Config, NewConfig(Guid.NewGuid()))
            .Add(x => x.CanEdit, true)
            .Add(x => x.OnSave, config => saved = config));

        cut.Find("div[data-property='DeviceType'] select").Change(((short)DeviceTypes.Storage).ToString());
        cut.Find("#property-panel-save-button").Click();

        Assert.Equal((short)DeviceTypes.Storage, saved!.DeviceType);
    }

    [Fact]
    public void ChoosingADeviceInAnItemReferenceFieldThenSavingWritesItsGuid()
    {
        var deviceId = Guid.NewGuid();
        var device = new PlantItemDto(deviceId, "Ricollocatore", DeviceTypes.Storage, null, null, null,
            default, default, 0, null, 0, 0, 0, 0, default!);
        PlantItemConfigDto? saved = null;

        var cut = Render<PropertyPanel>(p => p
            .Add(x => x.Config, NewConfig(Guid.NewGuid()))
            .Add(x => x.Items, [device])
            .Add(x => x.CanEdit, true)
            .Add(x => x.OnSave, config => saved = config));

        cut.Find("div[data-property='RelocateItemId'] select").Change(deviceId.ToString());
        cut.Find("#property-panel-save-button").Click();

        Assert.Equal(deviceId, saved!.RelocateItemId);
    }

    /// <summary>
    /// "Il pannello non inventa valori": un'immissione che il convertitore non sa interpretare (testo
    /// non numerico su una proprieta' int) non diventa zero - il campo resta quello che era in
    /// Config, non un dato mai immesso davvero.
    /// </summary>
    [Fact]
    public void AnUnparsableNumberIsIgnoredRatherThanSavedAsZero()
    {
        PlantItemConfigDto? saved = null;
        var cut = Render<PropertyPanel>(p => p
            .Add(x => x.Config, NewConfig(Guid.NewGuid()))
            .Add(x => x.CanEdit, true)
            .Add(x => x.OnSave, config => saved = config));

        cut.Find("div[data-property='TimeOutPlcMoving'] input").Change("non-un-numero");
        cut.Find("#property-panel-save-button").Click();

        Assert.Equal(1000, saved!.TimeOutPlcMoving); // il valore di Config, non 0
    }

    /// <summary>
    /// Cambiare Config (un dispositivo diverso scelto) sostituisce la copia di lavoro: le modifiche
    /// non salvate sul dispositivo precedente non restano appese sul nuovo.
    /// </summary>
    [Fact]
    public void ChangingTheConfigParameterReplacesTheWorkingCopyDiscardingUnsavedEdits()
    {
        var firstId = Guid.NewGuid();
        var cut = Render<PropertyPanel>(p => p
            .Add(x => x.Config, NewConfig(firstId))
            .Add(x => x.CanEdit, true));
        cut.Find("div[data-property='Name'] input").Change("modifica non salvata");

        var secondId = Guid.NewGuid();
        cut.Render(p => p.Add(x => x.Config, NewConfig(secondId)));

        Assert.Equal("dispositivo di prova", cut.Find("div[data-property='Name'] input").GetAttribute("value"));
    }

    /// <summary>
    /// I campi int/decimal (TimeOutPlcMoving, LinkedCellId) sono type="number", non type="text": il
    /// browser rifiuta un'immissione illeggibile prima che arrivi al convertitore, invece di lasciarla
    /// a video senza che il modello l'abbia accettata (revisione del compito 9). Le proprieta' che non
    /// passano da questo ramo con un tipo numerico (Name, string) restano type="text": non tutto il
    /// pannello e' cambiato, solo i campi per cui il rischio esiste.
    /// </summary>
    [Fact]
    public void IntAndDecimalFieldsRenderAsNativeNumberInputs()
    {
        var cut = Render<PropertyPanel>(p => p
            .Add(x => x.Config, NewConfig(Guid.NewGuid()))
            .Add(x => x.CanEdit, true));

        Assert.Equal("number", cut.Find("div[data-property='TimeOutPlcMoving'] input").GetAttribute("type"));
        Assert.Equal("number", cut.Find("div[data-property='LinkedCellId'] input").GetAttribute("type"));
        Assert.Equal("text", cut.Find("div[data-property='Name'] input").GetAttribute("type"));

        // The decimals are shown as integers: the arrows step by one unit. The ints keep the same
        // implicit step without a repeated attribute.
        Assert.Equal("1", cut.Find("div[data-property='LinkedCellId'] input").GetAttribute("step"));
        Assert.Null(cut.Find("div[data-property='TimeOutPlcMoving'] input").GetAttribute("step"));
    }

    [Fact]
    public void DecimalPropertiesAreDisplayedAndSavedAsTruncatedIntegersEvenWhenUntouched()
    {
        var config = NewConfig(Guid.NewGuid());
        config.LinkedCellId = 125.9m;
        config.DeviceConnectionId = -12.7m;
        config.CellToDefragmentId = 3.99m;
        PlantItemConfigDto? saved = null;
        var cut = Render<PropertyPanel>(p => p
            .Add(x => x.Config, config)
            .Add(x => x.CanEdit, true)
            .Add(x => x.OnSave, value => saved = value));

        Assert.Equal("125", cut.Find("[data-property='LinkedCellId'] input").GetAttribute("value"));
        Assert.Equal("-12", cut.Find("[data-property='DeviceConnectionId'] input").GetAttribute("value"));
        Assert.Equal("3", cut.Find("[data-property='CellToDefragmentId'] input").GetAttribute("value"));
        Assert.Equal(3, cut.FindAll("[data-property] input[type='number'][step='1']").Count);

        cut.Find("#property-panel-save-button").Click();

        Assert.Equal(125m, saved!.LinkedCellId);
        Assert.Equal(-12m, saved.DeviceConnectionId);
        Assert.Equal(3m, saved.CellToDefragmentId);
        Assert.Equal(125.9m, config.LinkedCellId);
        Assert.Equal(-12.7m, config.DeviceConnectionId);
        Assert.Equal(3.99m, config.CellToDefragmentId);
    }

    [Theory]
    [InlineData("99.9", "99")]
    [InlineData("-99.9", "-99")]
    [InlineData("7.9", "7")]
    public void ADecimalTypedWithAFractionIsTruncatedImmediately(string typed, string expected)
    {
        var config = NewConfig(Guid.NewGuid());
        config.LinkedCellId = 7m;
        PlantItemConfigDto? saved = null;
        var cut = Render<PropertyPanel>(p => p
            .Add(x => x.Config, config)
            .Add(x => x.CanEdit, true)
            .Add(x => x.OnSave, value => saved = value));

        var input = cut.Find("[data-property='LinkedCellId'] input");
        var generation = InputGeneration(cut.Instance, "LinkedCellId");
        input.Change(typed);
        var normalizedInput = cut.Find("[data-property='LinkedCellId'] input");
        Assert.Equal(expected, normalizedInput.GetAttribute("value"));
        // Even when the truncated value is unchanged, replace the input so a browser does not
        // keep the user's fractional text while Blazor sees an unchanged value attribute.
        Assert.NotEqual(generation, InputGeneration(cut.Instance, "LinkedCellId"));
        cut.Find("#property-panel-save-button").Click();
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), saved!.LinkedCellId);
    }

    [Fact]
    public void ConsecutiveIntegerDecimalEditsKeepTheInputSoKeyboardFocusIsNotRemoved()
    {
        var config = NewConfig(Guid.NewGuid());
        config.LinkedCellId = 7m;
        PlantItemConfigDto? saved = null;
        var cut = Render<PropertyPanel>(p => p
            .Add(x => x.Config, config)
            .Add(x => x.CanEdit, true)
            .Add(x => x.OnSave, value => saved = value));

        // Native number-input arrows emit consecutive integer changes on the focused input.
        // Keep the actual DOM element alive so the next keyboard increment can reach it.
        var input = cut.Find("[data-property='LinkedCellId'] input");
        var generation = InputGeneration(cut.Instance, "LinkedCellId");
        input.Change("8");
        Assert.Equal(generation, InputGeneration(cut.Instance, "LinkedCellId"));
        Assert.Equal("8", input.GetAttribute("value"));
        input.Change("9");
        Assert.Equal(generation, InputGeneration(cut.Instance, "LinkedCellId"));
        Assert.Equal("9", input.GetAttribute("value"));

        cut.Find("#property-panel-save-button").Click();
        Assert.Equal(9m, saved!.LinkedCellId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("79228162514264337593543950336")]
    public void ARejectedDecimalRestoresTheLastAcceptedTruncatedValue(string rejected)
    {
        var config = NewConfig(Guid.NewGuid());
        config.LinkedCellId = 12.9m;
        PlantItemConfigDto? saved = null;
        var cut = Render<PropertyPanel>(p => p
            .Add(x => x.Config, config)
            .Add(x => x.CanEdit, true)
            .Add(x => x.OnSave, value => saved = value));

        cut.Find("[data-property='LinkedCellId'] input").Change("99.9");
        cut.Find("[data-property='LinkedCellId'] input").Change(rejected);

        Assert.Equal("99", cut.Find("[data-property='LinkedCellId'] input").GetAttribute("value"));
        cut.Find("#property-panel-save-button").Click();
        Assert.Equal(99m, saved!.LinkedCellId);
    }

    /// <summary>
    /// RelocateItemId che punta a un dispositivo non (piu') nell'impianto: il pannello lo mostra nudo
    /// invece di farlo sparire dietro "(none)" o la prima voce dell'elenco - un dato che il modello
    /// porta ancora non deve sembrare assente a video.
    /// </summary>
    [Fact]
    public void AnItemReferenceThatPointsToADeviceNotInThePlantIsShownRatherThanHidden()
    {
        var orphanId = Guid.NewGuid();
        var config = NewConfig(Guid.NewGuid());
        config.RelocateItemId = orphanId;

        // L'impianto non porta orphanId: nessun dispositivo con quella chiave e' fra Items.
        var otherDevice = new PlantItemDto(Guid.NewGuid(), "Altro", DeviceTypes.Storage, null, null, null,
            default, default, 0, null, 0, 0, 0, 0, default!);

        var cut = Render<PropertyPanel>(p => p
            .Add(x => x.Config, config)
            .Add(x => x.Items, [otherDevice])
            .Add(x => x.CanEdit, true));

        var select = cut.Find("div[data-property='RelocateItemId'] select");
        var options = select.QuerySelectorAll("option").ToList();

        Assert.Contains(options, o => o.GetAttribute("value") == orphanId.ToString());
        Assert.Equal(orphanId.ToString(), select.GetAttribute("value"));
    }

    /// <summary>
    /// Minore della seconda passata: prima della correzione l'opzione orfana e un dispositivo
    /// legittimo senza nome mostravano entrambi il proprio Guid nudo e nient'altro - chi guarda non
    /// aveva modo di distinguerli. Qui la stessa configurazione porta entrambi i casi insieme:
    /// RelocateItemId orfano, e un dispositivo dell'impianto senza Name.
    /// </summary>
    [Fact]
    public void TheOrphanOptionIsTextuallyDistinctFromANamelessDevice()
    {
        var orphanId = Guid.NewGuid();
        var config = NewConfig(Guid.NewGuid());
        config.RelocateItemId = orphanId;

        var namelessDevice = new PlantItemDto(Guid.NewGuid(), null, DeviceTypes.Storage, null, null, null,
            default, default, 0, null, 0, 0, 0, 0, default!);

        var cut = Render<PropertyPanel>(p => p
            .Add(x => x.Config, config)
            .Add(x => x.Items, [namelessDevice])
            .Add(x => x.CanEdit, true));

        var options = cut.Find("div[data-property='RelocateItemId'] select").QuerySelectorAll("option").ToList();

        var orphanOption = options.Single(o => o.GetAttribute("value") == orphanId.ToString());
        var namelessOption = options.Single(o => o.GetAttribute("value") == namelessDevice.ItemId.ToString());

        Assert.NotEqual(orphanOption.TextContent, namelessOption.TextContent);
        Assert.Contains("(not in the plant)", orphanOption.TextContent);
        Assert.Equal(namelessDevice.ItemId.ToString(), namelessOption.TextContent);
    }

    /// <summary>
    /// DeviceType con un codice che l'enum non porta: EnumCodeConverter lo ammette apposta (il DTO
    /// puo' arrivare da una versione piu' recente dell'host), quindi il pannello lo mostra come cifra
    /// nuda invece di far scomparire il valore vero dietro la prima opzione dell'elenco.
    /// </summary>
    [Fact]
    public void AnEnumCodeNotInTheEnumIsShownRatherThanHiddenBehindTheFirstOption()
    {
        var config = NewConfig(Guid.NewGuid());
        config.DeviceType = 4711; // nessun DeviceTypes ha questo codice

        var cut = Render<PropertyPanel>(p => p
            .Add(x => x.Config, config)
            .Add(x => x.CanEdit, true));

        var select = cut.Find("div[data-property='DeviceType'] select");
        var options = select.QuerySelectorAll("option").ToList();

        Assert.Contains(options, o => o.GetAttribute("value") == "4711" && o.TextContent.Trim() == "4711");
        Assert.Equal("4711", select.GetAttribute("value"));
    }

    /// <summary>
    /// Una proprieta' bool (AutoExecuteStep) e' una checkbox, letta e scritta - ramo senza test prima
    /// di questa correzione.
    /// </summary>
    [Fact]
    public void ABooleanFieldRendersAsACheckboxReadableAndWritable()
    {
        var config = NewConfig(Guid.NewGuid());
        config.AutoExecuteStep = true;
        PlantItemConfigDto? saved = null;

        var cut = Render<PropertyPanel>(p => p
            .Add(x => x.Config, config)
            .Add(x => x.CanEdit, true)
            .Add(x => x.OnSave, c => saved = c));

        var checkbox = cut.Find("div[data-property='AutoExecuteStep'] input[type='checkbox']");
        Assert.True(checkbox.HasAttribute("checked"));

        checkbox.Change(false);
        cut.Find("#property-panel-save-button").Click();

        Assert.False(saved!.AutoExecuteStep);
    }

    /// <summary>
    /// Un Guid?/Guid non arricchito (BuildDestinationItemId: uno dei sei che restano casella di testo,
    /// non elenco - si veda PropertyModelTest) passa dal ramo testuale come gli int/decimal/string:
    /// letto e scritto per testo, non un elenco. Ramo senza test prima di questa correzione.
    /// </summary>
    [Fact]
    public void AnUnenrichedGuidFieldRendersAsATextInputReadableAndWritable()
    {
        var existingId = Guid.NewGuid();
        var config = NewConfig(Guid.NewGuid());
        config.BuildDestinationItemId = existingId;
        PlantItemConfigDto? saved = null;

        var cut = Render<PropertyPanel>(p => p
            .Add(x => x.Config, config)
            .Add(x => x.CanEdit, true)
            .Add(x => x.OnSave, c => saved = c));

        var input = cut.Find("div[data-property='BuildDestinationItemId'] input");
        Assert.Equal("text", input.GetAttribute("type"));
        Assert.Equal(existingId.ToString(), input.GetAttribute("value"));

        var newId = Guid.NewGuid();
        input.Change(newId.ToString());
        cut.Find("#property-panel-save-button").Click();

        Assert.Equal(newId, saved!.BuildDestinationItemId);
    }
}
