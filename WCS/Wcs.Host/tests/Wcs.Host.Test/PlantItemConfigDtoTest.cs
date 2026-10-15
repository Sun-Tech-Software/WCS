using Wcs.Api.Contracts.Dtos;
using Wcs.Engine.Persistence.Entities;
using Xunit;

namespace Wcs.Host.Test;

/// <summary>
/// Il DTO di configurazione rispecchia l'entità, nome per nome e tipo per tipo, tranne le tre
/// proprietà dal vivo e le sei della geometria/versionamento: se una colonna nasce o cambia, questo test lo dice.
/// </summary>
public sealed class PlantItemConfigDtoTest
{
    private static readonly string[] Live = ["Status", "LoadUnitStatus", "RunningStepId"];

    /// <summary>
    /// Le cinque colonne del disegno e il relativo token di versione: non sono configurazione, le
    /// scrive una rotta sua (PUT /plant/items/{id}/geometry e PUT /plant/items/geometry), e nei DTO
    /// compaiono in PlantItemDto, non in questo di configurazione.
    /// </summary>
    private static readonly string[] Geometry = ["X", "Y", "Width", "Height", "Rotation", "GeometryVersion"];

    [Fact]
    public void TheDtoMirrorsTheEntityExceptTheLiveProperties()
    {
        var entity = typeof(PlantItem).GetProperties().Where(p => !Live.Contains(p.Name) && !Geometry.Contains(p.Name)).ToDictionary(p => p.Name, p => p.PropertyType);
        var dto = typeof(PlantItemConfigDto).GetProperties().ToDictionary(p => p.Name, p => p.PropertyType);
        Assert.Equal(entity.Keys.Order(), dto.Keys.Order());
        foreach (var (name, type) in entity) Assert.Equal(type, dto[name]);
        Assert.All(typeof(PlantItemConfigDto).GetProperties(), p => Assert.True(p.CanWrite, p.Name));
    }
}
