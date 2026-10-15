namespace Wcs.Engine.Persistence.Entities;

/// <summary>
/// La riga di una missione nel database del WCS: quello che di <c>PathMission</c> sopravvive a un
/// riavvio. Sostituisce <c>MISSION_LU</c> su SQL Server.
/// </summary>
/// <remarks>
/// I nomi sono inglesi perche' lo schema nasce con il database del WCS; la corrispondenza con le
/// colonne di <c>MISSION_LU</c> e' documentata in <c>MissionStore</c>, che e' l'unico punto in cui
/// la riga e l'oggetto in memoria si incontrano. Gli identificatori vuoti (<c>Guid.Empty</c>, zero)
/// diventano nulli nella riga, come facevano i parametri SQL.
/// </remarks>
public sealed class Mission
{
    /// <summary>Chiave tecnica, crescente: da' l'ordine di creazione al riavvio.</summary>
    public long Id { get; set; }

    /// <summary>L'identita' della missione, la stessa dell'oggetto in memoria. Unica.</summary>
    public Guid MissionId { get; set; }

    public decimal LoadUnitId { get; set; }
    public Guid? PositionItemId { get; set; }
    public Guid? SourceItemId { get; set; }
    public Guid? DestinationItemId { get; set; }
    public DateTime Date { get; set; }
    public string? LoadUnitCode { get; set; }
    public string? StatusDescription { get; set; }
    public string? StorageDoorCode { get; set; }
    public string? PickUpDoorCode { get; set; }
    public short Status { get; set; }
    public decimal? CommandId { get; set; }
    public Guid? RelocateItemId { get; set; }
    public short MissionType { get; set; }
    public string? MovingDescription { get; set; }
    public short OperationType { get; set; }
    public decimal BufferCellId { get; set; }
    public decimal ExtractionQueueId { get; set; }
    public decimal LoadUnitTypeId { get; set; }
    public Guid? GroupedMovementId { get; set; }
    public int HandlingMode { get; set; }
    public Guid? GatewayItemId { get; set; }
    public int StorageDepth { get; set; }
    public string? SourceAsi { get; set; }
    public string? SourceSubitem { get; set; }
    public string? SourcePartition { get; set; }
    public string? DestinationAsi { get; set; }
    public string? DestinationSubitem { get; set; }
    public string? DestinationPartition { get; set; }
    public decimal NoteAssignmentId { get; set; }
    public Guid? AssignmentGuid { get; set; }
    public decimal NoteProcessingId { get; set; }
    public bool ActiveFilter { get; set; }
    public decimal Tare { get; set; }
    public bool ReallocatedDestination { get; set; }

    /// <summary>
    /// La destinazione l'ha scelta l'operatore (<c>PathMission.OperatorDestination</c>): la
    /// ricostruzione della rotta la rispetta invece di riapplicare la configurazione del
    /// dispositivo. Non ha una colonna in <c>MISSION_LU</c>; le righe che c'erano prima valgono
    /// «non scelta dall'operatore».
    /// </summary>
    public bool OperatorDestination { get; set; }

    /// <summary>
    /// Item 54, decision 5 of the owner of 13/10: what the relocator the operator sent the mission to does
    /// with the load unit (<c>PathMission.RelocatorArrival</c>, the values of <c>Api.RelocatorArrival</c>):
    /// 0 nothing chosen, 1 deposit it, 2 only bring it there. It stays after the arrival, so a restart
    /// with the load unit on the relocator does not hand it to the relocator's configuration. The rows
    /// written before item 54 read 0.
    /// </summary>
    public short RelocatorArrival { get; set; }

    /// <summary>
    /// La missione ha perso la sua cella di deposito e aspetta che il navigatore gliene prenoti
    /// un'altra nel magazzino della destinazione (<c>PathMission.StorageCellLost</c>): dopo un riavvio
    /// la ricarica non le costruisce la rotta verso il magazzino nudo. Non ha una colonna in
    /// <c>MISSION_LU</c>; le righe che c'erano prima valgono «cella non persa».
    /// </summary>
    public bool StorageCellLost { get; set; }

    /// <summary>
    /// La cella di deposito della missione e' una prenotazione che il motore ha fatto per lei presso
    /// il gestionale (<c>PathMission.StorageCellReserved</c>): dopo un riavvio il deposito chiede
    /// ancora se e' sua. Non ha una colonna in <c>MISSION_LU</c>.
    /// </summary>
    /// <remarks>
    /// Le righe che c'erano prima valgono «non sua», e la migrazione le lascia cosi': di una riga
    /// vecchia non si sa se la porta fosse una prenotazione della missione o no (l'ostacolo, lo
    /// spostamento manuale, l'UDC rimessa nella sua cella e le missioni di gruppo che copiano la
    /// porta di un'altra hanno una porta e nessuna prenotazione loro). Con «sua» il deposito di
    /// quelle chiederebbe al gestionale, che risponde «non c'e'», e la cella si rifarebbe altrove;
    /// con «non sua» le missioni in viaggio al momento dell'aggiornamento depositano come prima.
    /// Tranne le righe dell'operatore con una porta, che la migrazione porta a «sua»: prima del
    /// segno ogni cambio dell'operatore verso un magazzino prenotava la cella, e con il segno giu'
    /// su un membro di un gruppo sarebbero copie (<c>MissionGateway.IsACopy</c>), il cui deposito
    /// non chiede.
    /// </remarks>
    public bool StorageCellReserved { get; set; }

    /// <summary>
    /// La profondita' della cella di deposito prenotata mentre l'UDC e' ancora nella sua cella di
    /// partenza (<c>PathMission.StorageDepthAfterPickUp</c>): dopo un riavvio il prelievo legge ancora
    /// <see cref="StorageDepth"/>, la profondita' a cui l'UDC sta, e il deposito trova questa. Nulla se
    /// non ce n'e' una in attesa. Non ha una colonna in <c>MISSION_LU</c>; le righe che c'erano prima
    /// valgono «nessuna in attesa», cioe' com'era.
    /// </summary>
    public int? StorageDepthAfterPickUp { get; set; }

    /// <summary>
    /// Le segnalazioni del cambio cella sono partite (<c>PathMission.ChangeOfCellReportsSent</c>): il
    /// gestionale puo' aver saputo che l'estrazione lascia l'assegnazione e che l'UDC esce dalla coda
    /// di estrazione. Scritto prima della prima segnalazione e tolto dal commit del cambio; una riga
    /// che lo porta ed e' ancora un'estrazione si chiude alla ricarica. Non ha una colonna in
    /// <c>MISSION_LU</c>; le righe che c'erano prima valgono «nessuna segnalazione partita».
    /// </summary>
    public bool ChangeOfCellReportsSent { get; set; }

    /// <summary>
    /// La missione ha lasciato la porta che aveva copiato da un'altra missione del gruppo, che ha perso
    /// la cella, e aspetta che la ricerca per tipo del gruppo gliela rifaccia
    /// (<c>PathMission.LeftACopiedDoor</c>): dopo un riavvio il ciclo del gruppo la cerca ancora, in ogni
    /// modalita' di deposito, e la ricarica non le costruisce la rotta verso il magazzino nudo. Non ha
    /// una colonna in <c>MISSION_LU</c>; le righe che c'erano prima valgono «nessuna porta lasciata».
    /// </summary>
    public bool LeftACopiedDoor { get; set; }

    /// <summary>
    /// Le celle che il PLC ha rifiutato al deposito della missione (<c>PathMission.CellsRefusedByThePlc</c>),
    /// gli id separati da virgola nella cultura invariante: la ricarica le rimette, e ogni ricerca della
    /// missione le esclude ancora finche' l'UDC non deposita (risposta 2 del proprietario del 9/10). Nullo
    /// per nessuna; non ha una colonna in <c>MISSION_LU</c>, e le righe che c'erano prima valgono
    /// «nessuna cella rifiutata».
    /// </summary>
    public string? CellsRefusedByThePlc { get; set; }

    /// <summary>
    /// Il PLC ha rifiutato almeno un deposito della missione, anche verso una porta senza cella
    /// (<c>PathMission.DepositRefusedByThePlc</c>): lo scarto lo guarda per togliere il gateway. Torna
    /// con la ricarica insieme alle celle rifiutate; le righe che c'erano prima valgono «nessun rifiuto».
    /// </summary>
    public bool DepositRefusedByThePlc { get; set; }

    /// <summary>
    /// Che cosa ha servito il prelievo dell'estrazione (voce 45, <c>PathMission.ServedAssignmentId</c>,
    /// <c>ServedAssignmentGuid</c>, <c>ServedExtractionRowId</c>): l'assegnazione della nota, il suo
    /// GUID e la riga di estrazione per UDC, copiati prima che la missione si stacchi dalla riga di
    /// coda e dal GUID. Servono a chiedere al gestionale di riaprire l'ordine quando l'estrazione gia'
    /// prelevata va in un magazzino. Non hanno una colonna in <c>MISSION_LU</c>; le righe che c'erano
    /// prima valgono «niente servito» (zero, nullo, zero).
    /// </summary>
    public decimal ServedAssignmentId { get; set; }

    /// <inheritdoc cref="ServedAssignmentId"/>
    public Guid? ServedAssignmentGuid { get; set; }

    /// <inheritdoc cref="ServedAssignmentId"/>
    public decimal ServedExtractionRowId { get; set; }
}
