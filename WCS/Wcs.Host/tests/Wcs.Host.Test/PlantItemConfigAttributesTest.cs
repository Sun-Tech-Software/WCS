using System.ComponentModel;
using Wcs.Api.Contracts.Dtos;
using Xunit;

namespace Wcs.Host.Test;

/// <summary>
/// La griglia delle proprietà dell'Editor mostra il DTO: senza gli attributi di
/// <c>System.ComponentModel</c> diventa un elenco alfabetico senza gruppi, che è il difetto visto
/// sul campo. Le categorie sono quelle di <c>AutomationDevice</c> nel motore, e una categoria
/// nuova inventata per distrazione qui si vede subito.
/// </summary>
public sealed class PlantItemConfigAttributesTest
{
    private static readonly string[] EngineCategories =
    [
        "Basic Option", "Device Option", "Group Option", "Path Option",
        "Storing Option", "Defragment Option", "Test Options", "Live Data"
    ];

    [Fact]
    public void EveryPropertyHasACategoryAndADescription()
    {
        Assert.All(typeof(PlantItemConfigDto).GetProperties(), property =>
        {
            var category = property.GetCustomAttributes(typeof(CategoryAttribute), false) is [CategoryAttribute c] ? c.Category : null;
            var description = property.GetCustomAttributes(typeof(DescriptionAttribute), false) is [DescriptionAttribute d] ? d.Description : null;
            Assert.False(string.IsNullOrWhiteSpace(category), $"{property.Name} è senza [Category]");
            Assert.False(string.IsNullOrWhiteSpace(description), $"{property.Name} è senza [Description]");
            Assert.Contains(category, EngineCategories);
        });
    }

    /// <summary>
    /// I cinque <c>short</c> che portano un enum si leggono come testo: nel motore la griglia
    /// mostrava la descrizione del valore, e sul DTO ci pensa <c>EnumCodeConverter</c>.
    /// </summary>
    [Fact]
    public void TheEnumBackedCodesReadAsText()
    {
        var converter = TypeDescriptor.GetProperties(typeof(PlantItemConfigDto))[nameof(PlantItemConfigDto.ConnectionType)]!.Converter;

        Assert.Equal("Connected to PLC", converter.ConvertToString((short)1));
        Assert.Equal((short)0, converter.ConvertFromString("Test (No Communication to PLC)"));
        Assert.Equal([(short)0, (short)1], converter.GetStandardValues()!.Cast<short>());
    }
}
