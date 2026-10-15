using Log.Interface;
using Wcs.Engine.ORM;
using Wcs.Engine.Persistence.Entities;
using Wcs.Wms.Contracts;

namespace Wcs.Engine;

/// <summary>
/// Consegna al WMS gli esiti di estrazione gia' registrati nell'outbox locale. La cancellazione
/// segue una consegna riuscita - o un rifiuto definitivo del gestionale, riga per riga - quindi un
/// arresto fra rete e SQLite puo' reinviare lo stesso fatto: il contratto WMS e' idempotente e la
/// semantica resta at-least-once.
/// </summary>
internal sealed class PendingExtractionOutcomeDispatcher
{
    private static readonly Guid RetryKey = new("7ea9b5bf-c31d-4d73-a8bd-22b17dc6dc67");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly RetryBackoff _retry;
    private readonly int _batchSize;

    public PendingExtractionOutcomeDispatcher(TimeProvider? time = null, int batchSize = 100)
    {
        _retry = new RetryBackoff(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30), time);
        _batchSize = batchSize;
    }

    /// <summary>
    /// Prova una consegna senza bloccare chi trova gia' un invio in corso. Qualunque errore dopo la
    /// persistenza resta locale all'invio: l'outbox conserva il lotto per il prossimo tentativo.
    /// </summary>
    public async Task TryDispatchAsync()
    {
        if (_retry.IsWaiting(RetryKey)) return;
        if (!await _gate.WaitAsync(0)) return;

        try
        {
            if (_retry.IsWaiting(RetryKey)) return;

            var pending = await ExtractionOutcomeOutbox.PendingAsync(_batchSize);
            if (pending.Count == 0)
            {
                _retry.Forget(RetryKey);
                return;
            }

            var batch = TakeFirstWorkstation(pending);
            try
            {
                await Navigator.Warehouse!.ReportExtractionOutcomesAsync(RequestFor(batch));
                await ExtractionOutcomeOutbox.DeleteAsync(batch.Select(outcome => outcome.Id).ToArray());
            }
            catch (WarehouseUnavailableException rejected) when (rejected.IsPermanentRejection)
            {
                // IL GESTIONALE HA DETTO DI NO, e riprovare lo stesso lotto non cambia niente. Con una
                // postazione sola per WCS il lotto e' TUTTO l'outbox, quindi fino al 24/9/2026 una riga
                // rifiutata per sempre fermava la consegna di ogni esito successivo. Si riprova riga
                // per riga: le buone passano, e se ne va solo quella che il gestionale rifiuta da sola.
                await DeliverOneByOneAsync(batch);
            }

            _retry.Forget(RetryKey);
        }
        catch (Exception ex)
        {
            var attempts = _retry.RegisterFailure(RetryKey);
            if (attempts == 1 || attempts % 10 == 0)
            {
                try
                {
                    Navigator.ReportError(new InvalidOperationException(
                        $"Pending extraction outcome dispatch failed after {attempts} attempts.", ex));
                }
                catch
                {
                    // La diagnostica non puo' trasformare un errore gia' gestito in un guasto del
                    // chiamante: l'outbox resta durevole e il prossimo giro ritentera'.
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Azzera soltanto il freno in memoria: gli esiti durevoli non vengono mai toccati.</summary>
    public void Reset() => _retry.Clear();

    private static ReportExtractionOutcomesRequest RequestFor(IReadOnlyList<PendingExtractionOutcome> outcomes) =>
        new(0m, outcomes[0].WorkstationId,
            outcomes.Select(outcome => new ExtractionRowOutcome(outcome.ExtractionRowId, outcome.Status)).ToList());

    /// <summary>
    /// Il lotto e' stato rifiutato: si consegna una riga alla volta, per separare la riga che il
    /// gestionale non accetta dalle altre. Ogni riga consegnata esce dall'outbox subito, quindi un
    /// guasto transitorio a meta' - che risale al chiamante e finisce nel backoff come sempre -
    /// lascia indietro soltanto le righe non ancora provate.
    /// </summary>
    /// <remarks>
    /// <b>Una riga rifiutata da sola esce dall'outbox</b>, e il suo esito resta scritto solo nel
    /// registro, come errore. Non c'e' uno stato di scarto su questa tabella, e tenerla dentro
    /// vorrebbe dire riprovarla per sempre: il gestionale ha gia' risposto, e la risposta e' no.
    /// </remarks>
    private static async Task DeliverOneByOneAsync(List<PendingExtractionOutcome> batch)
    {
        foreach (var outcome in batch)
        {
            try
            {
                await Navigator.Warehouse!.ReportExtractionOutcomesAsync(RequestFor([outcome]));
            }
            catch (WarehouseUnavailableException rejected) when (rejected.IsPermanentRejection)
            {
                Navigator.WriteLog(
                    $"Esito di estrazione della riga {outcome.ExtractionRowId} ({outcome.Status}) RIFIUTATO " +
                    $"DEFINITIVAMENTE dal gestionale e tolto dall'outbox, dove non sarebbe mai passato: {rejected.Message}",
                    LogEntryType.Error);
            }

            await ExtractionOutcomeOutbox.DeleteAsync([outcome.Id]);
        }
    }

    private static List<PendingExtractionOutcome> TakeFirstWorkstation(List<PendingExtractionOutcome> pending)
    {
        var workstationId = pending[0].WorkstationId;
        return pending.TakeWhile(outcome => outcome.WorkstationId == workstationId).ToList();
    }
}
