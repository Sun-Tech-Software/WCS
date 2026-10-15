namespace Wcs.Engine.Persistence.Entities;

/// <summary>
/// Dove sta un dispositivo nel disegno: i cinque numeri che il motore custodisce e non legge.
/// Ogni campo nullo significa <b>non collocato</b>, mai l'origine.
/// </summary>
public sealed record ItemGeometry(double? X, double? Y, double? Width, double? Height, double? Rotation)
{
    /// <summary>Il dispositivo che non sta da nessuna parte.</summary>
    public static readonly ItemGeometry None = new(null, null, null, null, null);
}

/// <summary>
/// La geometria e il token che la identifica, letti dalla stessa riga e dallo stesso snapshot.
/// </summary>
public sealed record VersionedItemGeometry(ItemGeometry Geometry, Guid Version)
{
    public static readonly VersionedItemGeometry None = new(ItemGeometry.None, Guid.Empty);
}
