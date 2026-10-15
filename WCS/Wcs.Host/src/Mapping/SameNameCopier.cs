namespace Wcs.Host.Mapping;

/// <summary>Copia le proprietà con lo stesso nome e tipo fra due oggetti: il DTO di configurazione e l'entità sono gemelli per costruzione (lo garantisce PlantItemConfigDtoTest).</summary>
internal static class SameNameCopier
{
    public static TDest Copy<TSource, TDest>(TSource source, TDest destination)
    {
        var targets = typeof(TDest).GetProperties().Where(p => p.CanWrite).ToDictionary(p => p.Name);
        foreach (var property in typeof(TSource).GetProperties().Where(p => p.CanRead))
            if (targets.TryGetValue(property.Name, out var target) && target.PropertyType == property.PropertyType)
                target.SetValue(destination, property.GetValue(source));
        return destination;
    }
}
