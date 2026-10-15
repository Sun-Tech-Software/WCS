using Wcs.Engine.Persistence.Entities;

namespace Wcs.Engine.Api;

/// <summary>Una geometria nuova e la versione esatta che il chiamante intende sostituire.</summary>
public sealed record GeometryWrite(Guid ItemId, ItemGeometry Geometry, Guid ExpectedVersion);

/// <summary>Il token assegnato a una geometria confermata sullo store.</summary>
public sealed record GeometryWriteConfirmation(Guid ItemId, Guid GeometryVersion);

/// <summary>
/// Esito atomico di una scrittura geometrica: conferme, mancanti o conflitti sono mutuamente
/// esclusivi.
/// </summary>
public sealed class GeometryWriteOutcome
{
    private GeometryWriteOutcome(
        IReadOnlyList<GeometryWriteConfirmation> written,
        IReadOnlyList<Guid> missingItemIds,
        IReadOnlyList<Guid> conflictingItemIds)
    {
        Written = written;
        MissingItemIds = missingItemIds;
        ConflictingItemIds = conflictingItemIds;
    }

    public IReadOnlyList<GeometryWriteConfirmation> Written { get; }
    public IReadOnlyList<Guid> MissingItemIds { get; }
    public IReadOnlyList<Guid> ConflictingItemIds { get; }
    public bool Succeeded => MissingItemIds.Count == 0 && ConflictingItemIds.Count == 0;

    internal static GeometryWriteOutcome Success(IReadOnlyList<GeometryWriteConfirmation> written) =>
        new(written, [], []);

    internal static GeometryWriteOutcome Missing(IReadOnlyList<Guid> itemIds) =>
        new([], itemIds, []);

    internal static GeometryWriteOutcome Conflict(IReadOnlyList<Guid> itemIds) =>
        new([], [], itemIds);
}
