using static Wcs.Common.Enums;

namespace Wcs.Api.Contracts.Dtos;

/// <summary>
/// Un dispositivo dell'impianto: identità, configurazione principale e stato dal vivo. La
/// configurazione completa — tutte le colonne di <c>PlantItems</c> — arriva con il piano 3a-2,
/// che la scrive dall'Editor.
/// </summary>
public sealed record PlantItemDto(Guid ItemId, string? Name, DeviceTypes DeviceType, string? Asi, string? Partition,
    string? Subitem, ConnectionTypes ConnectionType, MotionTypes MotionType, decimal LinkedCellId,
    Guid? RelocateItemId, decimal DeviceConnectionId, int GroupItemId, int BelongingGroupId, int GroupLevelId,
    ItemStatus Live,
    double? X = null, double? Y = null, double? Width = null, double? Height = null, double? Rotation = null)
{
    /// <summary>
    /// Token opaco dell'ultima geometria letta. Non e' un parametro opzionale del costruttore:
    /// il generatore OpenAPI di .NET 10 non sa esportare un valore predefinito <see cref="Guid"/>.
    /// </summary>
    public Guid GeometryVersion { get; init; }
}
