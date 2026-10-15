namespace Wcs.Engine.Persistence.Entities;

/// <summary>
/// Il fatto «l'estrazione che questa missione aveva gia' prelevato e' diventata un deposito»
/// (<c>IWarehouse.ReportPickedExtractionBecameDepositAsync</c>, voce 45), registrato dal WCS al
/// commit del cambio e non ancora consegnato al gestionale; o, con la sola riga di estrazione,
/// «l'estrazione ancora nella sua cella e' diventata un cambio cella» (compito 4), registrato con
/// la cancellazione della riga di coda. Si consegna quando nessuna missione porta piu' l'UDC. La riga resta durante tutti i
/// tentativi con lo stesso <see cref="OperationId"/>, che e' la chiave della ricevuta di Kazam.
/// </summary>
/// <remarks>
/// Gemella di <see cref="PendingExtractionOutcome"/> e non di <see cref="PendingWarehouseTransition"/>
/// (specifica della voce 45, sezione 3.5 punto 3): la riapertura non e' una mutazione fisica e non
/// deve trattenere le transizioni della stessa UDC. I campi sono quelli della richiesta, scritti per
/// colonna: niente payload da leggere, quindi niente payload illeggibile.
/// </remarks>
public sealed class PendingPickedExtractionReport
{
    public long Id { get; set; }
    public Guid OperationId { get; set; }
    public Guid MissionId { get; set; }
    public decimal LoadUnitId { get; set; }

    /// <summary><c>ID_ASSEGNAZIONE_UDC</c> confermato al prelievo, 0 se nessuno.</summary>
    public decimal AssignmentId { get; set; }

    /// <summary><c>GUID_ASSEGNAZIONE_UDC</c> segnalato «servito», vuoto se nessuno.</summary>
    public Guid AssignmentGuid { get; set; }

    /// <summary>
    /// <c>ID_RIGA_ESTRAZIONE_UDC</c> segnato «estratta», o nel cambio cella quello che la coda di
    /// estrazione stava servendo; 0 se nessuna.
    /// </summary>
    public decimal ExtractionRowId { get; set; }

    public string UserId { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }

    /// <summary>
    /// Il gestionale l'ha rifiutata per sempre (un 4xx): resta nella coda con il motivo in
    /// <see cref="LastError"/>, perche' qualcuno la veda e decida, ma non si ritenta piu'.
    /// </summary>
    public bool Parked { get; set; }
}
