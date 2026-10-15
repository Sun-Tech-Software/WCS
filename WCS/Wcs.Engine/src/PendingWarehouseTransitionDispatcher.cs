using Log.Interface;
using System.Text.Json;
using Wcs.Engine.ORM;
using Wcs.Engine.Persistence.Entities;
using Wcs.Wms.Contracts;

namespace Wcs.Engine;

/// <summary>
/// Consegna in ordine le transizioni fisiche già persistite. Una sola consegna può essere in
/// corso; al primo elemento non completabile la coda si ferma per non invertirne gli effetti.
/// </summary>
internal sealed class PendingWarehouseTransitionDispatcher
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly RetryBackoff _retry;
    private readonly TimeProvider _time;
    private readonly int _batchSize;

    public PendingWarehouseTransitionDispatcher(TimeProvider? time = null, int batchSize = 100)
    {
        _time = time ?? TimeProvider.System;
        _retry = new RetryBackoff(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30), _time);
        _batchSize = batchSize;
    }

    public async Task TryDispatchAsync()
    {
        if (!await _gate.WaitAsync(0)) return;

        try
        {
            foreach (var transition in await WarehouseTransitionOutbox.DueAsync(_time.GetUtcNow(), _batchSize))
            {
                if (_retry.IsWaiting(transition.OperationId)) break;

                try
                {
                    await DeliverAsync(transition);
                    if (!await WarehouseTransitionCompletion.CompleteAsync(transition)) break;

                    await WarehouseTransitionOutbox.DeleteAsync(transition.Id);
                    _retry.Forget(transition.OperationId);
                }
                catch (Exception failure)
                {
                    var attempts = _retry.RegisterFailure(transition.OperationId);
                    // UN RIFIUTO NON GUARISCE RIPROVANDO. Un payload che non si legge, o un 4xx del
                    // gestionale, si riprovavano con il backoff per sempre - e con l'ordine globale di
                    // prima fermavano tutta la coda. Adesso si parcheggiano: restano nell'outbox con il
                    // loro motivo, non tornano piu' dovute, e trattengono soltanto cio' che dipende da
                    // loro (PendingWarehouseTransitionStore.OldestDueAsync).
                    var permanent = IsPermanent(failure);
                    await WarehouseTransitionOutbox.RecordFailureAsync(
                        transition.Id,
                        failure,
                        permanent
                            ? Persistence.PendingWarehouseTransitionStore.ParkedUntil
                            : _retry.NextAttemptAt(transition.OperationId));
                    WarehouseTransitionCompletion.MarkPending(transition, failure, attempts);
                    if (permanent)
                        Navigator.WriteLog(
                            $"Transizione WMS {transition.OperationId} ({transition.Kind}, UDC {transition.LoadUnitId}) " +
                            $"RIFIUTATA DEFINITIVAMENTE e parcheggiata: non verra' ritentata. Trattiene le transizioni " +
                            $"della stessa UDC{(transition.Kind == WarehouseTransitionKind.Release ? " e tutte le allocazioni successive" : string.Empty)} " +
                            $"finche' non la si risolve a mano. Motivo: {failure.Message}",
                            LogEntryType.Error);
                    break;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Reset() => _retry.Clear();

    /// <summary>
    /// Il gestionale ha detto di no, o il payload non si legge: riprovare non cambia niente. Tutto il
    /// resto - una connessione caduta, un timeout, un 5xx, il gestionale non configurato - puo'
    /// guarire, e resta nel backoff come prima.
    /// </summary>
    private static bool IsPermanent(Exception failure) =>
        failure is JsonException or WarehouseUnavailableException { IsPermanentRejection: true };

    private static Task DeliverAsync(PendingWarehouseTransition transition)
    {
        var warehouse = Navigator.RequireWarehouse(
            $"transizione WMS {transition.OperationId} ({transition.Kind})");

        return transition.Kind switch
        {
            WarehouseTransitionKind.Allocate => warehouse.AllocateLoadUnitAsync(
                DeserializeAllocate(transition)),
            WarehouseTransitionKind.Release => warehouse.ReleaseLoadUnitAsync(
                DeserializeRelease(transition)),
            _ => throw new InvalidOperationException($"Unknown warehouse transition kind {transition.Kind}.")
        };
    }

    private static T Deserialize<T>(PendingWarehouseTransition transition) =>
        JsonSerializer.Deserialize<T>(transition.PayloadJson)
        ?? throw new JsonException($"Empty payload for warehouse transition {transition.OperationId}.");

    private static AllocateLoadUnitRequest DeserializeAllocate(PendingWarehouseTransition transition)
    {
        var request = Deserialize<AllocateLoadUnitRequest>(transition);
        ValidateOperationId(transition, request.OperationId);
        return request;
    }

    private static ReleaseLoadUnitRequest DeserializeRelease(PendingWarehouseTransition transition)
    {
        var request = Deserialize<ReleaseLoadUnitRequest>(transition);
        ValidateOperationId(transition, request.OperationId);
        return request;
    }

    private static void ValidateOperationId(PendingWarehouseTransition transition, Guid payloadOperationId)
    {
        if (payloadOperationId != transition.OperationId)
            throw new JsonException(
                $"Warehouse transition {transition.OperationId} contains payload operation {payloadOperationId}.");
    }
}
