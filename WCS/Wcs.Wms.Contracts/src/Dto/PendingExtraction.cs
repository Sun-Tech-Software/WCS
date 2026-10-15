namespace Wcs.Wms.Contracts;

/// <summary>
/// Una testata di estrazione per unità di carico pronta per l'automazione (compito 15): le sue righe
/// da servire, già decise dal gestionale. Qui il motore non assegna niente - non lo faceva nemmeno
/// prima - e non decide più nemmeno quali lavorazioni processare: riceve del lavoro già scelto, ne
/// risolve il movimento, e riferisce l'esito di ogni riga con
/// <see cref="IWarehouse.ReportExtractionOutcomesAsync"/>.
/// </summary>
/// <param name="ExtractionHeaderId">
/// <c>ID_TESTATA_ESTRAZIONE_UDC</c>. Obbligatorio: è la chiave con cui il motore riferisce gli esiti
/// della testata, ed è uno dei due campi che il ciclo del motore leggeva con <c>GetValueOrThrow</c>
/// perché una query sbagliata li aveva resi sempre zero e il processo non partiva mai (voci R10 e
/// R11 del registro in <c>WCS/doc/debito-tecnico.md</c>). Se non viaggiasse qui, quel difetto
/// tornerebbe senza più la lettura severa che lo ha fatto vedere.
/// </param>
/// <param name="PickingCellId">
/// <c>ID_CELLA_PICKING</c>, la cella polmone di destinazione. Obbligatorio per la stessa ragione:
/// è l'altro dei due campi delle voci R10 e R11.
/// </param>
/// <param name="BufferAreaIds">
/// Le aree servite dal polmone (ex <c>DbInfo.PickingBufferAreas</c>, ora letta dal gestionale): il
/// motore le usa per restringere la ricerca dell'unità di carico. Una testata il cui polmone non
/// serve nessuna area non arriva affatto - il ciclo del motore usciva senza scrivere nulla.
/// </param>
/// <param name="Rows">Le righe da servire, nell'ordine di <c>ID_RIGA_ESTRAZIONE_UDC</c>.</param>
public sealed record PendingExtraction(
    decimal ExtractionHeaderId,
    decimal PickingCellId,
    IReadOnlyList<decimal> BufferAreaIds,
    IReadOnlyList<PendingExtractionRow> Rows);
