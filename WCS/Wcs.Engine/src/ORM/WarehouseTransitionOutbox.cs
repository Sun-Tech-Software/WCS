using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;

namespace Wcs.Engine.ORM;

/// <summary>Facciata serializzata dell'outbox locale delle transizioni fisiche verso il WMS.</summary>
internal static class WarehouseTransitionOutbox
{
    public static async Task<PendingWarehouseTransition> EnqueueAsync(
        Guid operationId,
        WarehouseTransitionKind kind,
        Guid missionId,
        decimal loadUnitId,
        string payloadJson,
        DateTimeOffset? createdAtUtc = null)
    {
        EnsureStore();
        PendingWarehouseTransition? persisted = null;
        await ExtractionQueue.WriteAsync(async context =>
            persisted = await PendingWarehouseTransitionStore.EnqueueAsync(
                context,
                operationId,
                kind,
                missionId,
                loadUnitId,
                payloadJson,
                createdAtUtc ?? DateTimeOffset.UtcNow));
        return persisted ?? throw new InvalidOperationException("The warehouse transition was not persisted.");
    }

    public static Task<List<PendingWarehouseTransition>> DueAsync(DateTimeOffset nowUtc, int take)
    {
        EnsureStore();
        return ExtractionQueue.ReadAsync(
            context => PendingWarehouseTransitionStore.OldestDueAsync(context, nowUtc, take),
            new List<PendingWarehouseTransition>());
    }

    public static Task RecordFailureAsync(long id, Exception failure, DateTimeOffset nextAttemptAtUtc)
    {
        EnsureStore();
        return ExtractionQueue.WriteAsync(context =>
            PendingWarehouseTransitionStore.RecordFailureAsync(context, id, failure.ToString(), nextAttemptAtUtc));
    }

    public static Task DeleteAsync(long id)
    {
        EnsureStore();
        return ExtractionQueue.WriteAsync(context => PendingWarehouseTransitionStore.DeleteAsync(context, id));
    }

    public static Task<bool> HasPendingAllocateAsync(Guid missionId)
    {
        EnsureStore();
        return ExtractionQueue.ReadAsync(
            context => PendingWarehouseTransitionStore.HasPendingAsync(
                context, missionId, WarehouseTransitionKind.Allocate),
            false);
    }

    private static void EnsureStore()
    {
        if (Navigator.Store == null)
            throw new InvalidOperationException("The WCS operational store is not available.");
    }
}
