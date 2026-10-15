using Log.Interface;
using Wcs.Engine.ORM;
using Wcs.Engine.Persistence.Entities;
using Wcs.Wms.Contracts;

namespace Wcs.Engine;

/// <summary>
/// Consegna al gestionale il fatto «l'estrazione e' diventata un deposito o un cambio cella»
/// (voce 45): l'estrazione prelevata mandata in un magazzino (compito 3) e quella ancora nella sua
/// cella diventata un cambio cella (compito 4), registrato nella coda locale al cambio. La riga esce dopo una risposta del
/// gestionale, qualunque sia l'esito nel corpo: «riaperto», «niente da riaprire» e «ordine gia'
/// chiuso» sono risposte, non guasti, e riprovarle non cambia niente. Un guasto del trasporto, un
/// timeout, un 5xx o il gestionale non cablato lasciano la riga dov'e' e si ritenta al giro dopo,
/// con il freno; un rifiuto definitivo (un 4xx) la parcheggia con il motivo. Un arresto fra la
/// risposta e la cancellazione rimanda lo stesso <c>OperationId</c>: la ricevuta di Kazam ne fa un
/// colpo a vuoto, e la semantica resta at-least-once.
/// </summary>
/// <remarks>
/// <para>
/// <b>Dopo l'«estratta».</b> Una riga la cui riga di estrazione ha ancora l'esito «estratta» nella
/// coda degli esiti (<see cref="PendingExtractionOutcomeDispatcher"/>) aspetta: consegnata prima, la
/// riapertura arriverebbe al gestionale prima dell'«estratta», che poi riscriverebbe la riga come
/// prelevata (specifica della voce 45, sezione 3.4). Il ciclo master chiama prima la coda degli
/// esiti e poi questa (<c>Navigator.MasterProcess</c>).
/// </para>
/// <para>
/// <b>A missione finita</b> (decisione A del proprietario dell'11/10). Una riga la cui UDC e'
/// ancora portata da una missione (<see cref="Navigator.IsLuInExtraction"/>: le attive, il
/// prelievo di un dispositivo, le code di prelievo) aspetta, senza contare un tentativo: il
/// deposito o il cambio cella devono essere finiti. Consegnata prima, il gestionale rimetterebbe
/// la riga di estrazione da servire mentre l'UDC e' ancora in missione, e il giro di assegnazione
/// (<c>Navigator.ServeExtraction</c>) la riscriverebbe «in estrazione» senza costruire la coda,
/// perche' i costruttori escono in silenzio per un'UDC in missione: la riga resterebbe ferma come
/// prima. Vale anche quando l'UDC prende subito un'altra missione: la riapertura aspetta anche
/// quella. Il controllo legge le collezioni del motore senza lucchetti, prima della chiamata.
/// </para>
/// <para>
/// Nessun lucchetto e' preso mentre il gestionale risponde: solo il cancello di questo dispatcher,
/// senza attesa, perche' due giri non mandino la stessa riga insieme.
/// </para>
/// </remarks>
internal sealed class PendingPickedExtractionReportDispatcher
{
    private static readonly Guid RetryKey = new("b1d4c045-7e2a-4c1b-9a45-0d3c5e7f9045");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly RetryBackoff _retry;
    private readonly int _batchSize;

    public PendingPickedExtractionReportDispatcher(TimeProvider? time = null, int batchSize = 100)
    {
        _retry = new RetryBackoff(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30), time);
        _batchSize = batchSize;
    }

    /// <summary>
    /// Prova una consegna senza bloccare chi trova gia' un invio in corso. Non solleva: un guasto
    /// lascia la riga nella coda per il prossimo giro.
    /// </summary>
    public async Task TryDispatchAsync()
    {
        if (_retry.IsWaiting(RetryKey)) return;
        if (!await _gate.WaitAsync(0)) return;

        PendingPickedExtractionReport? current = null;
        try
        {
            if (_retry.IsWaiting(RetryKey)) return;

            foreach (var report in await PickedExtractionReportOutbox.PendingAsync(_batchSize))
            {
                // L'UDC e' ancora su una missione: il fatto aspetta che la missione finisca.
                if (Navigator.IsLuInExtraction(report.LoadUnitId))
                    continue;

                if (report.ExtractionRowId > 0 &&
                    await PickedExtractionReportOutbox.ExtractionOutcomeStillPendingAsync(report.ExtractionRowId))
                    continue;

                current = report;
                PickedExtractionBecameDepositResult result;
                try
                {
                    result = await Navigator.RequireWarehouse($"riapertura dell'estrazione della missione {report.MissionId}")
                        .ReportPickedExtractionBecameDepositAsync(RequestFor(report));
                }
                catch (WarehouseUnavailableException rejected) when (rejected.IsPermanentRejection)
                {
                    await PickedExtractionReportOutbox.RecordFailureAsync(report.Id, rejected, park: true);
                    Navigator.WriteLog(
                        $"Missione {report.MissionId}, UDC {report.LoadUnitId:0}: la riapertura dell'ordine (operazione {report.OperationId}) " +
                        $"e' stata RIFIUTATA DEFINITIVAMENTE dal gestionale e resta parcheggiata nella coda del WCS: non verra' ritentata, " +
                        $"e l'ordine va sistemato a mano nel gestionale. Motivo: {rejected.Message}",
                        LogEntryType.Error);
                    continue;
                }

                WriteTheOutcome(report, result);
                await PickedExtractionReportOutbox.DeleteAsync(report.Id);
                current = null;
            }

            _retry.Forget(RetryKey);
        }
        catch (Exception ex)
        {
            var attempts = _retry.RegisterFailure(RetryKey);
            try
            {
                if (current != null)
                    await PickedExtractionReportOutbox.RecordFailureAsync(current.Id, ex, park: false);
            }
            catch
            {
                // Il conto dei tentativi e' diagnostica: la riga resta comunque nella coda.
            }

            if (attempts == 1 || attempts % 10 == 0)
            {
                try
                {
                    Navigator.ReportError(new InvalidOperationException(
                        $"Picked extraction reopen dispatch failed after {attempts} attempts.", ex));
                }
                catch
                {
                    // La diagnostica non trasforma un errore gia' gestito in un guasto del chiamante.
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Azzera soltanto il freno in memoria: le righe durevoli non vengono mai toccate.</summary>
    public void Reset() => _retry.Clear();

    private static PickedExtractionBecameDepositRequest RequestFor(PendingPickedExtractionReport report) =>
        new(report.OperationId, report.MissionId, report.LoadUnitId, report.AssignmentId, report.AssignmentGuid,
            report.ExtractionRowId, report.UserId, report.Reason);

    /// <summary>
    /// L'esito nel registro: un'informazione se tutto quello che c'era e' stato riaperto, un avviso se
    /// il gestionale non ha riaperto qualcosa che la missione aveva servito («niente da riaprire»,
    /// «ordine gia' chiuso»), perche' li' l'operatore decide nel gestionale. «Niente da riaprire» per
    /// una parte che la missione non aveva (assegnazione 0, riga di estrazione 0) e' la risposta
    /// attesa, e non e' un avviso; «ordine gia' chiuso» lo e' sempre.
    /// </summary>
    private static void WriteTheOutcome(PendingPickedExtractionReport report, PickedExtractionBecameDepositResult result)
    {
        var allReopened = AsExpected(result.Assignment, report.AssignmentId > 0) &&
                          AsExpected(result.ExtractionRow, report.ExtractionRowId > 0);
        var detail = string.IsNullOrWhiteSpace(result.Detail) ? string.Empty : $" ({result.Detail})";
        Navigator.WriteLog(
            $"Missione {report.MissionId}, UDC {report.LoadUnitId:0}: alla riapertura di quello che la missione serviva " +
            $"il gestionale risponde: assegnazione {report.AssignmentId:0} {result.Assignment}, " +
            $"riga di estrazione {report.ExtractionRowId:0} {result.ExtractionRow}{detail}" +
            (allReopened ? string.Empty : "; quello che non e' stato riaperto va guardato nel gestionale"),
            allReopened ? LogEntryType.Information : LogEntryType.Warning);

        static bool AsExpected(PickedExtractionReopenOutcome outcome, bool served) => outcome switch
        {
            PickedExtractionReopenOutcome.Reopened => true,
            PickedExtractionReopenOutcome.NothingToReopen => !served,
            _ => false
        };
    }
}
