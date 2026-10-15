namespace Wcs.Engine.Persistence.Entities;

/// <summary>
/// Una UDC movimentata: che cosa le e' successo in una missione — caricata, estratta, rigettata —
/// con la cella, il codice e le lavorazioni del WMS che l'hanno chiesta. Una riga per missione,
/// nello storico mensile. Sostituisce <c>LU_HANDLED</c> su SQL Server.
/// </summary>
/// <remarks>
/// La riga nasce quando la missione parte e cambia finche' la missione vive; per questo la chiave
/// logica e' la missione, non una chiave tecnica che il chiamante dovrebbe aspettare dal database.
/// Corrispondenza con le vecchie colonne: <c>MISSION_ID</c>→<c>MissionId</c>, <c>TYPE</c>→<c>Type</c>,
/// <c>ID_CELLA</c>→<c>CellId</c>, <c>COD_LU</c>→<c>LoadUnitCode</c>, <c>ID_LAVORAZIONE</c>→<c>ProcessId</c>,
/// <c>RESULT_DESCRIPTION</c>→<c>ResultDescription</c>, <c>ID_LAVORAZIONE_UDC</c>→<c>LoadUnitProcessId</c>,
/// <c>ID_TESTATA_NOTA</c>→<c>NoteHeaderId</c>, <c>ID_TESTATA_ESTRAZIONE_UDC</c>→<c>ExtractionHeaderId</c>,
/// <c>LU_ID</c>→<c>LoadUnitId</c>. I riferimenti alle lavorazioni del WMS sono numeri, non chiavi
/// esterne: lo storico del WCS non impedisce al WMS di cancellare le proprie righe (decisione del
/// 2026-09-02).
/// </remarks>
public sealed class HandledLoadUnit
{
    public long Id { get; set; }
    public Guid MissionId { get; set; }
    public short Type { get; set; }
    public DateTime Date { get; set; }
    public decimal CellId { get; set; }
    public string? LoadUnitCode { get; set; }
    public decimal? ProcessId { get; set; }
    public string? ResultDescription { get; set; }
    public decimal? LoadUnitProcessId { get; set; }
    public decimal? NoteHeaderId { get; set; }
    public decimal? ExtractionHeaderId { get; set; }
    public decimal? LoadUnitId { get; set; }
}
