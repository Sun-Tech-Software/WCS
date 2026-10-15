namespace Wcs.Engine.Persistence.Entities;

/// <summary>
/// Una riga della coda di estrazione e ricollocamento: una UDC da prendere da una porta e portare
/// a una destinazione, oppure un ostacolo da ricollocare. Sostituisce
/// <c>ESTRAZIONI_UDC_AUTOMATICHE</c> su SQL Server.
/// </summary>
/// <remarks>
/// La scrive solo il motore (i costruttori di <c>ExecutableCommands</c> e <c>BuildLuExtraction</c>)
/// e la consuma il ciclo di estrazione di ogni magazzino. Corrispondenza con le vecchie colonne:
/// <c>ID_ESTRAZIONE_UDC</c>→<c>Id</c>, <c>ID_ASSEGNAZIONE_UDC</c>→<c>AssignmentId</c>,
/// <c>ID_UDC</c>→<c>LoadUnitId</c>, <c>ID_CELLA</c>→<c>CellId</c>, <c>ID_CELLA_POLMONE</c>→<c>BufferCellId</c>,
/// <c>ID_ITEM_SOURCE</c>→<c>SourceItemId</c>, <c>ID_ITEM_DESTINATION</c>→<c>DestinationItemId</c>,
/// <c>ESTRARRE</c>→<c>Extract</c> (falso: e' un ricollocamento), <c>ID_POSTAZIONE</c>→<c>WorkstationId</c>,
/// <c>ELABORATA</c>→<c>Processed</c>, <c>ID_ITEM_RELOCATOR</c>→<c>RelocatorItemId</c>,
/// <c>CODICE_PORTA</c>→<c>DoorCode</c>, <c>ID_SOURCE_ASSEGNAZIONE_UDC</c>→<c>SourceAssignmentId</c>,
/// <c>ID_RIGA_ESTRAZIONE_UDC</c>→<c>ExtractionRowId</c>, <c>ID_CELLA_DESTINAZIONE</c>→<c>DestinationCellId</c>,
/// <c>CODICE_PORTA_DESTINAZIONE</c>→<c>DestinationDoorCode</c>, <c>PRIORITA</c>→<c>Priority</c>,
/// <c>COMPATTAMENTO</c>→<c>Compacting</c>, <c>GUID_ASSEGNAZIONE_UDC</c>→<c>AssignmentGuid</c>,
/// <c>ID_COMMAND_TODO_API</c>→<c>ApiCommandId</c>.
/// </remarks>
public sealed class ExtractionQueueItem
{
    /// <summary>Chiave crescente: e' l'ordine della coda, e le missioni la ricordano come <c>ExtractionQueueId</c>.</summary>
    public long Id { get; set; }

    public decimal? AssignmentId { get; set; }
    public decimal LoadUnitId { get; set; }
    public decimal? CellId { get; set; }
    public decimal? BufferCellId { get; set; }
    public Guid SourceItemId { get; set; }
    public Guid? DestinationItemId { get; set; }
    public bool Extract { get; set; } = true;
    public string? WorkstationId { get; set; }
    public bool Processed { get; set; }
    public Guid? RelocatorItemId { get; set; }
    public string? DoorCode { get; set; }
    public decimal? SourceAssignmentId { get; set; }
    public decimal? ExtractionRowId { get; set; }

    /// <summary>
    /// La lavorazione di estrazione servita da <see cref="ExtractionRowId"/> (compito 8): arriva con
    /// la riga pendente (<c>PendingExtractionRow.ProcessingId</c>) e resta qui, invece di essere
    /// riletta dal gestionale a ogni chiusura di movimento come faceva
    /// <c>DbInfo.GetIdLavorazione</c>. Zero significa «questa riga non serve nessuna lavorazione di
    /// estrazione»: e' il caso del ricollocamento di un ostacolo e quello della riga nata da
    /// un'assegnazione di nota, la cui lavorazione sta in <see cref="NoteProcessingId"/> (compito 9)
    /// e non qui - sono due tabelle diverse del gestionale. Viaggia in coppia con
    /// <see cref="ExtractionRowId"/>: le due si scrivono e si cambiano insieme.
    /// <para>
    /// <b>Dal compito 12 questo campo si scrive ma non si legge piu'.</b> Il suo unico lettore era
    /// <c>DbInfo.UpdateStatusExtractionLu</c>, che lo passava alla chiusura della lavorazione; adesso
    /// la lavorazione la ricava il gestionale dalle righe segnalate (<c>LavorazioniDelleRigheSql</c>,
    /// compito 11) e il motore non ha piu' bisogno di conoscerla. Non e' stato tolto in questo
    /// compito perche' toglierlo vuol dire una migrazione, e una migrazione non viaggia di
    /// contrabbando: e' una voce da aprire. Il gemello <see cref="NoteProcessingId"/> invece resta
    /// vivo e letto anche dopo il compito 13, ma per altre due ragioni - vedi la nota sua.
    /// </para>
    /// </summary>
    public decimal ProcessingId { get; set; }

    /// <summary>
    /// La lavorazione della <b>nota di spedizione</b> servita da <see cref="AssignmentId"/> (compito
    /// 9): arriva con l'assegnazione pronta (<c>PendingNote.ProcessingId</c>,
    /// <c>TESTATE_NOTE.ID_LAVORAZIONE</c>) e resta qui, invece di essere riletta dal gestionale a
    /// ogni giro di ogni ricollocatore come faceva <c>DbInfo.ProcessingIdsByAssignment</c>. Di qui
    /// arriva alla missione (<c>PathMission.NoteProcessingId</c>), che e' chi decide se la nota e'
    /// evasa. Zero significa «questa riga non serve nessuna nota»: e' il caso del ricollocamento di
    /// un ostacolo e della riga nata da un'estrazione per unita' di carico. Viaggia in coppia con
    /// <see cref="AssignmentId"/>, e si scrivono insieme.
    /// <para>
    /// <b>E' un campo diverso da <see cref="ProcessingId"/>, non lo stesso con due nomi.</b> Le due
    /// lavorazioni stanno su due tabelle del gestionale (<c>LAVORAZIONI_NOTE</c> e
    /// <c>LAVORAZIONI_ESTRAZIONI_UDC</c>) che condividono lo spazio degli identificativi, e si
    /// chiudono con due <c>UPDATE</c> diversi. Un campo solo per tutt'e due farebbe chiudere alla
    /// fine di ogni movimento di nota la lavorazione di estrazione omonima: fino al compito 12
    /// <c>UpdateStatusExtractionLu</c> passava <see cref="ProcessingId"/> alla chiusura per
    /// <b>ogni</b> riga di coda che si chiudeva, anche quella nata da un'assegnazione; e la
    /// ridirezione di una unita' di carico su un altro polmone sovrascrive <see cref="ProcessingId"/>
    /// senza sapere niente di note.
    /// </para>
    /// <para>
    /// <b>Dal compito 13 questo campo non serve piu' alla chiusura della nota</b>, che e' passata al
    /// gestionale e si ricava la lavorazione dall'<see cref="AssignmentId"/>. Resta vivo e letto per
    /// due decisioni che sono del motore e di nessun altro: la missione di gruppo non mette insieme
    /// unita' di carico di lavorazioni diverse (<c>AutomationDeviceGroupManager</c>), e la scelta
    /// del polmone di destinazione cambia quando cambia la lavorazione
    /// (<c>AutomationDeviceLuManager</c>). Non ha quindi il problema del gemello
    /// <see cref="ProcessingId"/>, che invece si scrive e non si legge piu'.
    /// </para>
    /// </summary>
    public decimal NoteProcessingId { get; set; }
    public decimal? DestinationCellId { get; set; }
    public string? DestinationDoorCode { get; set; }
    public int Priority { get; set; }
    public bool Compacting { get; set; }
    public Guid? AssignmentGuid { get; set; }
    public decimal? ApiCommandId { get; set; }
}
