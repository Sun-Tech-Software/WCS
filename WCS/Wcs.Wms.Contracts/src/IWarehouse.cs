namespace Wcs.Wms.Contracts;

/// <summary>
/// What the engine asks the warehouse management system (WMS), or tells it: forty-three domain
/// operations, one HTTP route each (<see cref="WarehouseRoutes"/>). The storage reservation of a
/// mission takes three of them because it has a life cycle (reserve, read and renew, release); the
/// others take one each. There is no operation to assign work or to close a processing: the WMS
/// decides both, and the engine only reads the work that is ready and reports what happened to it
/// (<see cref="ReportExtractionOutcomesAsync"/>). The routes, the casing of the bodies and the
/// meaning of each status code are described in the integration guide (<c>wms-contract.md</c>).
/// Every method raises <see cref="WarehouseUnavailableException"/> when the WMS does not answer.
/// </summary>
public interface IWarehouse
{
    /// <summary>Che cosa c'è scritto su questa cella: area, profondità, blocco, tipo ammesso.</summary>
    Task<CellDescription> DescribeCellAsync(decimal cellId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Prenota una destinazione di deposito per la missione (compito 12, ex
    /// <c>CelleMagazzino.CercaDestinazioneUdc</c>). Non è una ricerca: quando
    /// <see cref="ReserveStorageRequest.AssignPosition"/> è vero l'operazione scrive - è l'unica del
    /// percorso caldo che lo fa. L'esito (dodici valori) viaggia nella risposta, non nell'eccezione:
    /// solo un guasto del gestionale solleva <see cref="WarehouseUnavailableException"/>, «nessuna
    /// cella libera» è una risposta di dominio.
    /// </summary>
    Task<StorageReservation> ReserveStorageAsync(ReserveStorageRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// La prenotazione di deposito che questa missione tiene, se ne tiene ancora una (compito 17).
    /// La prenotazione <b>appartiene alla missione</b>: resta valida mentre la missione aspetta, e a
    /// ogni tentativo successivo il motore chiede qui invece di riprenotare - una prenotazione e'
    /// una scrittura in transazione attraverso la rete, la chiamata piu' costosa di questo confine, e
    /// riscriverla a ogni tentativo la si paga proprio quando l'impianto e' gia' in difficolta'.
    /// <para>
    /// <see langword="null"/> vuol dire «questa missione non ha una prenotazione»: e' una risposta di
    /// dominio, non un guasto (un 404 sul filo), e vale sia per la missione che non ne ha mai avuta
    /// una sia per quella la cui prenotazione e' stata revocata - la cella se l'e' presa qualcun
    /// altro, o l'assegnazione e' stata cancellata da un'altra strada del gestionale. In tutt'e due
    /// i casi il motore riprenota.
    /// </para>
    /// <para>
    /// <b>Chiedere rinnova la prenotazione.</b> Il gestionale scade le prenotazioni orfane sull'età,
    /// e questa domanda è il segno che una missione viva è ancora interessata alla sua cella: la
    /// risposta e il rinnovo sono la stessa operazione. Non è quindi una lettura senza effetti, ed è
    /// l'unico modo che il motore ha di dire «questa prenotazione è ancora di qualcuno» senza che il
    /// gestionale debba chiedergli quali missioni siano vive - una domanda che resterebbe senza
    /// risposta proprio quando il WCS è spento, cioè il caso per cui la scadenza esiste.
    /// </para>
    /// </summary>
    Task<StorageReservation?> GetStorageReservationAsync(Guid missionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// La missione lascia la sua prenotazione di deposito, e la cella torna libera per le altre
    /// (compito 17). <b>Idempotente:</b> rilasciare due volte non e' un errore, e una missione che
    /// non aveva niente da lasciare non e' un caso da segnalare - il motore chiama questa operazione
    /// su ogni missione che si chiude o viene annullata, senza sapere se ne avesse una.
    /// </summary>
    Task ReleaseStorageReservationAsync(Guid missionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The WCS gives back a storage cell it had reserved for a load unit (item 54, answer 1 of the owner
    /// of 13/10): the warehouse releases the reservation only if it still holds that cell for that load
    /// unit, and never the place where the load unit actually is. Idempotent: a cell no longer held is
    /// <see cref="LoadUnitCellReleaseOutcome.NotHeld"/>, a domain answer.
    /// </summary>
    /// <remarks>
    /// Sent by a durable queue of the WCS (<c>PendingCellGiveBacks</c>), retried until the warehouse
    /// answers: a change of cell or a defragmentation that the operator redirects lets go of the cell it
    /// was going to, including the reservations taken without a mission (the obstacle, the reload),
    /// which <see cref="ReleaseStorageReservationAsync"/> does not reach. The warehouse deletes the load
    /// unit's assignment to that cell and the mission's link to it, both or none. Raises
    /// <see cref="WarehouseUnavailableException"/> if the warehouse does not answer.
    /// </remarks>
    Task<LoadUnitCellReleaseOutcome> ReleaseLoadUnitCellAsync(ReleaseLoadUnitCellRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// The storage cells suited to a load unit (item 54, decision 6): the first
    /// <see cref="StorageCellCandidatesRequest.Limit"/> of the search of every day, in its order, each
    /// named by its door A code. A reading: nothing is reserved, and an empty list is an answer («no cell
    /// suits it»). Raises <see cref="WarehouseUnavailableException"/> if the warehouse does not answer.
    /// </summary>
    Task<IReadOnlyList<StorageCellCandidate>> ListStorageCellCandidatesAsync(StorageCellCandidatesRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reserve a precise cell for the load unit (item 54, decision 6): the warehouse checks that the cell
    /// is free and suited to it - the search of every day would propose it - and reserves it, otherwise
    /// it refuses with the reason. The refusals are answers in the body
    /// (<see cref="StorageCellReservationResult.Outcome"/>); only a failure raises
    /// <see cref="WarehouseUnavailableException"/>.
    /// </summary>
    /// <remarks>
    /// The reservation replaces the one the load unit had, as every reservation of the warehouse does
    /// (one assigned position per load unit), and with a mission it is that mission's reservation
    /// (<see cref="GetStorageReservationAsync"/>).
    /// </remarks>
    Task<StorageCellReservationResult> ReserveStorageCellAsync(ReserveStorageCellRequest request, CancellationToken cancellationToken = default);

    /// <summary>Dove si trova questa unità di carico, e a che profondità.</summary>
    Task<LoadUnitLocation?> LocateLoadUnitAsync(decimal loadUnitId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Le note di spedizione pronte per l'automazione (compito 14): il gestionale decide quali note
    /// assegnare, assegna, e valida da sé contro le tre cose che solo il WCS sa - questa operazione
    /// risponde con le righe già assegnate e già servibili. Niente operazione per assegnare:
    /// assegnare non è più cosa del WCS.
    /// </summary>
    /// <param name="limitOneShippingNotePerBuffer">
    /// <c>Navigator.LimitOneShippingNotePerBuffer</c>, l'opzione d'impianto che vieta di assegnare
    /// una nota quando un'altra lavorazione attiva insiste sulla stessa cella di picking. È
    /// un'opzione del WCS e resta nella configurazione dell'host del WCS, ma la decisione che
    /// governa vive dall'altra parte del confine, quindi viaggia nella richiesta.
    /// </param>
    Task<IReadOnlyList<PendingNote>> GetPendingNoteWorkAsync(bool limitOneShippingNotePerBuffer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Scarta una delle righe pronte, con il motivo (compito 14, decisione 3): il gestionale
    /// cancella l'assegnazione e scrive il motivo nel proprio registro, dove finiscono già gli
    /// altri tre rifiuti. È il quarto motivo - «l'unità di carico non è disponibile nelle aree
    /// servite dal polmone» - il solo che il gestionale non può applicare da sé: per scoprirlo
    /// bisogna cercare l'unità di carico nel vivo del diagramma, e quello lo sa fare solo il
    /// motore. Il fatto lo scopre il WCS, la conseguenza la esegue il gestionale: le assegnazioni
    /// le fa e le disfà lui.
    /// <para>
    /// <b>Dal compito 13 i punti che la chiamano sono cinque, non uno.</b> Gli altri quattro sono
    /// quelli in cui il motore scarta un'assegnazione che non può più servire perché la riga di
    /// coda che la portava se ne va: due nel ciclo di sfondo del magazzino (l'unità di carico è già
    /// in missione per un'altra riga di estrazione, o per un'altra assegnazione) e due nelle
    /// pulizie periodiche della coda (la lavorazione dietro la riga non è più in corso). Prima
    /// cancellavano l'assegnazione da sé, con un <c>DELETE</c> su <c>ASSEGNAZIONE_NOTE</c>. Sono la
    /// stessa operazione e non due, perché condividono chiave (<c>ID_ASSEGNAZIONE_UDC</c>), forma e
    /// semantica - «questa assegnazione di nota il motore non la serve» - e cambia solo il motivo,
    /// che è quello che un operatore legge.
    /// </para>
    /// </summary>
    Task RejectNoteAssignmentAsync(RejectNoteAssignmentRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fra queste assegnazioni di nota, quali appartengono a una lavorazione che <b>non è più in
    /// corso</b> (compito 6, ex le due query dirette su <c>ASSEGNAZIONE_NOTE</c>,
    /// <c>RIGHE_NOTE</c>, <c>TESTATE_NOTE</c> e <c>LAVORAZIONI_NOTE</c> che il motore eseguiva in
    /// <c>DbInfo.DeleteLuInExtranctionByPausedNote</c> e <c>DbInfo.DeleteQueueLuOfNoteDone</c>)?
    /// Chi la chiama ne trae una cosa sola: quelle righe escono dalla coda di estrazione del WCS,
    /// che è nel database del WCS. Il chiamante nel motore è <b>uno solo</b>,
    /// <c>DbInfo.DeleteLuInExtranctionByPausedNote</c>: la revisione del compito 6 ha cancellato
    /// <c>DbInfo.DeleteQueueLuOfNoteDone</c>, che poneva questa stessa domanda una seconda volta
    /// nello stesso giro del motore.
    /// <para>
    /// <b>È la gemella di <see cref="GetPausedExtractionRowsAsync"/></b>, e resta un'operazione a
    /// sé perché la chiave non è la stessa: qui gli identificativi sono assegnazioni di nota, di là
    /// righe di estrazione, e a rispondere sono due catene di tabelle diverse. A coincidere è la
    /// forma della domanda, ed è per questo che le due condividono <see cref="PausedAmongRequest"/>
    /// e non la rotta.
    /// </para>
    /// <para>
    /// <b>La domanda parte dalla coda, non dal gestionale.</b> È il motore a dire di quali
    /// assegnazioni si parla - sono quelle che ha in coda - e il gestionale risponde solo su
    /// quelle. Il secondo chiamante faceva il contrario: leggeva <b>tutta</b>
    /// <c>ASSEGNAZIONE_NOTE</c> senza nessuna chiave, cioè le assegnazioni sospese dell'intero
    /// impianto, per poi cancellare le righe di coda che gli corrispondevano (difetto C2). Con
    /// questa forma la domanda ha una chiave, e la risposta è grande quanto la coda.
    /// </para>
    /// <para>
    /// <b>Lo spezzettamento in blocchi è del gestionale</b>, come per la gemella: il limite di
    /// 2.100 parametri per comando è di SQL Server, cioè di chi esegue. Un elenco vuoto risponde
    /// una lista vuota senza che nessuno dei due lati tocchi il database.
    /// </para>
    /// <para>
    /// Lettura <b>severa</b>: la risposta è una lista, e una lista vuota significa «nessuna di
    /// queste è in pausa», che è un dato. Un gestionale che non risponde solleva
    /// <see cref="WarehouseUnavailableException"/> - dove <c>PausedAmongAsync</c> registrava
    /// l'errore e rispondeva <see langword="null"/>, che il chiamante leggeva come «nessuna», cioè
    /// una pulizia saltata in silenzio.
    /// </para>
    /// <para>
    /// La risposta è un insieme: l'ordine non è garantito, e un identificativo ripetuto in
    /// ingresso può comparire una volta sola. Chi legge confronta per appartenenza.
    /// </para>
    /// </summary>
    /// <param name="assignmentIds">
    /// Le assegnazioni da esaminare (<c>ASSEGNAZIONE_NOTE.ID_ASSEGNAZIONE_UDC</c>).
    /// </param>
    Task<IReadOnlyList<decimal>> GetPausedNoteAssignmentsAsync(IReadOnlyList<decimal> assignmentIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// <b>Una segnalazione, non un ordine:</b> questa unita' di carico ha lasciato la coda di
    /// estrazione del WCS, e con se' l'assegnazione che quella riga di coda stava servendo. Il fatto
    /// e' del WCS - la coda e' nel suo database - e le conseguenze le trae il gestionale: oggi toglie
    /// la posizione prenotata dell'unita' di carico e fa tornare «non assegnata» la nota
    /// dell'assegnazione, domani quello che decidera'.
    /// <para>
    /// <b>Era in due operazioni imperative</b> (compito 19: <c>ClearLoadUnitReservedPositionAsync</c>
    /// e <c>MarkNoteUnassignedAsync</c>), che il motore chiamava una dopo l'altra dallo stesso punto
    /// dicendo al gestionale che cosa scrivere - e per formularle doveva conoscerne il modello di
    /// dati. Sono lo stesso fatto, e nessuna delle due avviene senza di lui. <b>Due chiamanti</b>:
    /// <c>DbInfo.DeleteLuFromExtractionQueue</c>, a sua volta chiamato solo dal comando manuale che
    /// toglie un'unita' di carico dalla coda, e dalla voce 39 il cambio cella: l'estrazione che l'operatore manda in un altro
    /// magazzino con l'unita' di carico ancora nella sua cella esce dalla coda, prima che il motore
    /// prenoti la cella nuova - dopo, la posizione prenotata che si toglie sarebbe proprio quella.
    /// </para>
    /// <para>
    /// <b>Le due conseguenze non sono simmetriche, e la differenza e' nel fatto stesso:</b> la
    /// posizione prenotata si toglie sempre, la nota torna assegnabile solo se la riga di coda
    /// stava servendo un'assegnazione. Il <c>se</c> viaggia nel campo
    /// <see cref="LoadUnitLeftExtractionQueueRequest.AssignmentId"/> - <c>0</c> quando non ce n'era
    /// nessuna - e la decisione che ne segue e' del gestionale, non piu' un <c>if</c> del motore.
    /// </para>
    /// <para>
    /// <b>Idempotente e senza esito.</b> Un'assegnazione che non risolve nessuna nota, o
    /// un'unita' di carico senza posizioni prenotate, non sono casi da segnalare.
    /// </para>
    /// </summary>
    Task ReportLoadUnitLeftExtractionQueueAsync(LoadUnitLeftExtractionQueueRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Le testate di estrazione per unità di carico pronte per l'automazione, con le loro righe già
    /// scelte (compito 15). Qui il motore non assegnava niente nemmeno prima - la lista delle unità
    /// di carico gli arrivava già fatta - ma decideva quali lavorazioni processare e quando chiuderle:
    /// due decisioni sul lavoro di magazzino, che ora vivono di là. Niente operazione per chiudere
    /// una lavorazione: la chiusura è una conseguenza che il gestionale trae dagli stati che scrive
    /// (vedi <see cref="ReportExtractionOutcomesAsync"/>).
    /// <para>
    /// Nessun parametro: a differenza di <see cref="GetPendingNoteWorkAsync"/> questo ciclo non ha
    /// un'opzione d'impianto da far viaggiare.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<PendingExtraction>> GetPendingExtractionWorkAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Fra queste righe di estrazione, quali appartengono a una lavorazione che <b>non è più in
    /// corso</b> (compito 5, ex la query diretta su <c>RIGHE_ESTRAZIONI_UDC</c>,
    /// <c>TESTATE_ESTRAZIONI_UDC</c> e <c>LAVORAZIONI_ESTRAZIONI_UDC</c> che il motore eseguiva in
    /// <c>DbInfo.DeleteLuInExtranctionByPausedExtractionLu</c>)? Chi la chiama ne trae una cosa
    /// sola: quelle righe escono dalla coda di estrazione del WCS, che è nel database del WCS.
    /// <para>
    /// <b>La domanda parte dalla coda, non dal gestionale.</b> È il motore a dire di quali
    /// identificativi si parla - sono i suoi - e il gestionale risponde solo su quelli: leggere
    /// tutte le righe sospese dell'impianto per poi confrontarle di qua sono 422.317 righe a ogni
    /// giro, ed è il difetto che questa forma ha già chiuso quando la query era diretta.
    /// </para>
    /// <para>
    /// <b>Lo spezzettamento in blocchi è del gestionale.</b> Il limite di 2.100 parametri per
    /// comando è di SQL Server, cioè di chi esegue: il motore manda la lista intera e non sa più
    /// niente di blocchi. Un elenco vuoto risponde una lista vuota senza che nessuno dei due lati
    /// tocchi il database.
    /// </para>
    /// <para>
    /// Lettura <b>severa</b>: la risposta è una lista, e una lista vuota significa «nessuna di
    /// queste è in pausa», che è un dato. Un gestionale che non risponde solleva
    /// <see cref="WarehouseUnavailableException"/> - dove <c>PausedAmongAsync</c> registrava
    /// l'errore e rispondeva <see langword="null"/>, che i chiamanti leggevano come «nessuna», cioè
    /// una pulizia saltata in silenzio.
    /// </para>
    /// <para>
    /// La risposta e' un insieme: l'ordine non e' garantito, e un identificativo ripetuto in
    /// ingresso puo' comparire una volta sola. Chi legge confronta per appartenenza.
    /// </para>
    /// </summary>
    /// <param name="extractionRowIds">
    /// Le righe di estrazione da esaminare (<c>RIGHE_ESTRAZIONI_UDC.ID_RIGA_ESTRAZIONE_UDC</c>).
    /// </param>
    Task<IReadOnlyList<decimal>> GetPausedExtractionRowsAsync(IReadOnlyList<decimal> extractionRowIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Che cosa è successo alle righe di una testata di estrazione in questo giro (compito 15, ex la
    /// scrittura diretta di <c>DISTINTA_ESTRAZIONI_UDC</c> dal motore). Il gestionale scrive gli
    /// stati e poi, da sé, chiude la lavorazione se non resta più nulla da estrarre.
    /// <para>
    /// La chiave è la testata, non la riga, e un elenco di esiti vuoto è una risposta legittima: il
    /// perché sta su <see cref="ReportExtractionOutcomesRequest"/>.
    /// </para>
    /// </summary>
    Task ReportExtractionOutcomesAsync(ReportExtractionOutcomesRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// <b>Una segnalazione, non un ordine:</b> la missione che portava questa assegnazione e' uscita
    /// dal motore senza servirla (compito 21). Il fatto e' del WCS - le missioni sono sue - e la
    /// conseguenza la trae il gestionale: oggi cancella le righe di assegnazione, domani quello che
    /// deciderà.
    /// <para>
    /// <b>Quattro punti del motore, un fatto solo</b>: la missione tolta dalle attive, quella di
    /// compattazione che non trova piu' l'unità di carico nella cella di partenza, quella ricaricata
    /// all'avvio che risulta già a destinazione, e dalla voce 39 il cambio cella (qui sotto). Chiave
    /// uguale (la missione), forma uguale e semantica uguale; i due identificativi diversi con cui la
    /// missione nomina l'assegnazione sono due campi suoi, non due chiavi - vedi
    /// <see cref="MissionLeftAssignmentUnservedRequest"/>.
    /// </para>
    /// <para>
    /// <b>Il quarto punto, dalla voce 39:</b> l'estrazione che l'operatore manda in un altro magazzino
    /// con l'unità di carico ancora nella sua cella diventa un cambio cella, e lascia l'assegnazione
    /// senza servirla restando nel motore. Per il gestionale e' lo stesso fatto, con la stessa forma;
    /// arriva insieme a <see cref="ReportLoadUnitLeftExtractionQueueAsync"/>, prima che il motore
    /// prenoti la cella nuova.
    /// </para>
    /// </summary>
    Task ReportMissionLeftAssignmentUnservedAsync(MissionLeftAssignmentUnservedRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// <b>Il fatto opposto</b> (compito 21): l'unità di carico è arrivata alla postazione e
    /// l'assegnazione che la missione portava è stata servita. Stessa chiave di
    /// <see cref="ReportMissionLeftAssignmentUnservedAsync"/> e quasi la stessa forma, semantica
    /// opposta: due punti d'accesso stanno nella stessa operazione solo se condividono tutt'e tre.
    /// </summary>
    Task ReportMissionServedAssignmentAsync(MissionServedAssignmentRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// <b>Una segnalazione senza missione</b> (compito 21): una riga della coda di estrazione del WCS
    /// non si può servire - l'unità di carico è già impegnata in un'altra lavorazione, o la riga non
    /// ne ha nessuna - e l'assegnazione che stava aspettando resta senza chi la porti. La coda è nel
    /// database del WCS, quindi il fatto è suo; la conseguenza sull'assegnazione è del gestionale.
    /// </summary>
    Task ReportUnservableQueuedExtractionAsync(UnservableQueuedExtractionRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// L'unità di carico di questa assegnazione è arrivata a destinazione: conferma le assegnazioni
    /// che ne sono state servite (compito 19, ex <c>DbInfo.GetAssignedDataByExtractionId</c> per
    /// intero). Il gestionale trova da sé le righe di distinta servite - era una
    /// <c>SELECT DISTINCT</c> su tre sue tabelle che il motore eseguiva - e le conferma una per una.
    /// <para>
    /// <b>È l'unica operazione di questo compito che elimina anche una query SQL diretta</b>, e
    /// quindi la sola che riduce davvero quello che il motore sa del database del gestionale. Di
    /// qua resta la sola cosa che è del WCS: l'assegnazione, che il motore legge nella propria coda
    /// di estrazione.
    /// </para>
    /// <para>
    /// <b>Solleva se il gestionale non risponde</b>, e su questo percorso è un cambiamento: la
    /// versione nel motore inghiottiva l'errore - registrava una riga e proseguiva, senza marcare
    /// nessuno stato. La regola del confine conserva le degradazioni solo dove il codice marca uno
    /// stato che il motore già modella e mostra, e qui non era il caso.
    /// </para>
    /// <para>
    /// <b>È idempotente</b>, come le tre cancellazioni che lo dichiarano: una seconda chiamata con
    /// la stessa assegnazione non scrive una seconda conferma. Il gestionale <b>consuma</b>
    /// l'assegnazione che ha confermato - la toglie da <c>ASSEGNAZIONE_NOTE</c>, la cui chiave
    /// primaria è proprio l'assegnazione - quindi al secondo giro non trova più niente da
    /// confermare. Senza, la quantità della nota veniva contata due volte.
    /// </para>
    /// <para>
    /// <b>Che il motore ritenti davvero, però, non è detto</b> (voce m6 della revisione
    /// dell'ondata 1). Qui c'era scritto «se la risposta HTTP si perde ... al giro dopo richiama
    /// con la stessa assegnazione», e il commento di <c>PathMission.ProcessExtractionItems</c>
    /// diceva l'opposto - «la riga resta in coda e la conferma non viene ritentata». Il vero è il
    /// secondo: su un guasto la riga di coda del WCS resta (la cancellazione sta dopo la chiamata),
    /// ma <c>ProcessExtractionItems</c> azzera <c>ExtractionQueueId</c> nel proprio <c>finally</c>,
    /// e subito dopo <c>ProcessAssignments</c> segnala «assegnazione servita», la cui conseguenza
    /// è la cancellazione dell'assegnazione intera per GUID. Quella missione non richiama, e
    /// l'assegnazione se ne va comunque. L'idempotenza resta il contratto giusto - il confine non
    /// può dipendere da quante volte il chiamante chiama - ma non poggia più su una premessa che
    /// il codice non conferma.
    /// </para>
    /// </summary>
    Task ConfirmServedAssignmentsAsync(ConfirmServedAssignmentsRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// <b>Una segnalazione, non un ordine</b> (voce 45): l'estrazione che questa missione aveva gia'
    /// prelevato e' stata mandata dall'operatore in un magazzino (voce 39) ed e' diventata un
    /// deposito, quindi non arrivera' alla postazione. Il prelievo aveva gia' detto al gestionale
    /// che l'assegnazione di nota era servita e la riga di estrazione per unita' di carico estratta;
    /// la conseguenza - riaprirle, o lasciarle come sono se l'ordine e' gia' chiuso - e' sua.
    /// <para>
    /// <b>Lo stesso fatto per il cambio cella prima del prelievo</b> (voce 45, compito 4; decisione
    /// del proprietario dell'11/10): l'estrazione con l'unita' di carico ancora nella sua cella,
    /// mandata in un altro magazzino, diventa un cambio cella. Le segnalazioni del cambio cella
    /// lasciano l'assegnazione, ma la riga di estrazione per unita' di carico che il gestionale aveva
    /// messo «in estrazione» resterebbe ferma: la richiesta porta allora solo quella
    /// (<see cref="PickedExtractionBecameDepositRequest.ExtractionRowId"/> valorizzato, assegnazione 0
    /// e GUID vuoto), e il gestionale la rimette da servire anche se non era ancora segnata estratta.
    /// </para>
    /// <para>
    /// <b>A missione finita.</b> Il WCS la manda solo quando nessuna sua missione porta piu' l'unita'
    /// di carico (il deposito o il cambio cella sono finiti, o la missione e' chiusa): una riga rimessa
    /// da servire prima tornerebbe subito «in estrazione» senza poterla servire.
    /// </para>
    /// <para>
    /// <b>Parte dopo il cambio</b>, da una coda durevole del WCS: prima il fatto non e'
    /// ancora vero, e dopo il cambio non si disfa per un problema di contabilita' degli ordini. La
    /// coda consegna almeno una volta, e il gestionale riconosce le ripetizioni con
    /// <see cref="PickedExtractionBecameDepositRequest.OperationId"/>.
    /// </para>
    /// <para>
    /// <b>L'esito sta nel corpo</b> (<see cref="PickedExtractionBecameDepositResult"/>), uno per
    /// l'assegnazione e uno per la riga di estrazione: «riaperto», «niente da riaprire» e «ordine gia'
    /// chiuso» sono tutte risposte, e la coda le toglie. Solleva
    /// <see cref="WarehouseUnavailableException"/> se il gestionale non risponde, e con
    /// <see cref="WarehouseUnavailableException.IsPermanentRejection"/> vero se rifiuta la richiesta
    /// (un 409 per lo stesso identificativo con un contenuto diverso).
    /// </para>
    /// </summary>
    Task<PickedExtractionBecameDepositResult> ReportPickedExtractionBecameDepositAsync(PickedExtractionBecameDepositRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deposita fisicamente l'unità di carico nella cella - operatore o stoccaggio secondo
    /// <see cref="AllocateLoadUnitRequest.StorageDoorCode"/> (compito 13). Non transazionale: vedi
    /// la voce D118 di <c>WCS/doc/debito-tecnico.md</c>.
    /// </summary>
    Task AllocateLoadUnitAsync(AllocateLoadUnitRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// L'unità di carico esce dall'automazione: libera l'assegnazione, cancella la posizione fisica
    /// e la marca spedita (compito 13). Non transazionale: stessa voce D118.
    /// </summary>
    Task ReleaseLoadUnitAsync(ReleaseLoadUnitRequest request, CancellationToken cancellationToken = default);

    /// <summary>Le celle e le aree automatiche di un magazzino. Cambia di rado.</summary>
    Task<AreaTopology> GetAreaTopologyAsync(decimal warehouseId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Dov'è l'unità di carico, filtrata per aree o celle ammesse e per stato di blocco (compito 11,
    /// ex <c>DbInfo.OperazioniUdc.TrovaUdc</c>). Null se non è stata trovata nei limiti chiesti: è una
    /// risposta di dominio, non un guasto - la ricerca è per sua natura parziale (aree servite da un
    /// polmone, celle servite da un ricollocatore).
    /// </summary>
    Task<LoadUnitSearchResult?> FindLoadUnitAsync(FindLoadUnitRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Quale unità di carico ha questa referenza, e dove sta (compito 20, ex
    /// <c>CelleMagazzino.TrovaReferenza</c> chiamata dal motore, che ne prendeva il primo risultato).
    /// <para>
    /// <b>Non è <see cref="FindLoadUnitAsync"/> con un parametro in più</b>, benché la risposta abbia
    /// la stessa forma: quella chiede «dove sta <i>questa</i> unità di carico», questa chiede
    /// «<i>quale</i> unità di carico ha questa referenza, raggiungibile da questo polmone». Chiave
    /// diversa, semantica diversa: due punti d'accesso stanno nella stessa operazione solo se
    /// condividono chiave, forma e semantica.
    /// </para>
    /// <para>
    /// La richiesta porta due soli campi perché la decisione si è spostata per intero: le opzioni di
    /// ricerca del gestionale (aree selezionabili, tipo di operatore, celle da cercare) erano valori
    /// fissi che il motore costruiva, e le aree servite dal polmone erano una sua lettura su una
    /// tabella del gestionale. Ora si formulano di là. L'esito viaggia nel corpo, non nello stato
    /// HTTP: vedi <see cref="ReferenceSearchOutcome"/>.
    /// </para>
    /// </summary>
    Task<ReferenceSearchResult> FindLoadUnitByReferenceAsync(FindLoadUnitByReferenceRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// La porta di prelievo per questa unità di carico in questa cella, il suo codice, e gli ostacoli
    /// che le stanno davanti (compito 11, ex <c>Navigator.PickUpDoor</c> lato motore). Sostituisce il
    /// conteggio degli ostacoli di prima: quello era indicizzato per sola unità di carico, questa
    /// operazione lo è per unità di carico e cella, la chiave vera - un chiamante confrontava due
    /// porte diverse, e con la vecchia chiave quel confronto non era esprimibile.
    /// </summary>
    Task<PickUpDoorChoice> ChoosePickUpDoorAsync(decimal loadUnitId, decimal cellId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Le partizioni della cella, con l'offset di ciascuna e le colonne ASI/SUBITEM delle sue due
    /// porte (compito 8): sostituisce l'indirizzo di campo per codice porta, la profondità della
    /// cella e la profondità di stoccaggio per indirizzo, tre letture che leggevano la stessa
    /// tabella con chiavi diverse.
    /// </summary>
    Task<CellPartitionMap> GetCellPartitionMapAsync(decimal cellId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Le celle che rispondono a questo codice porta (compito 9): di solito una, ma
    /// <c>CODICE_PORTA_A</c>/<c>CODICE_PORTA_B</c> non hanno un vincolo di unicità nello schema (lo
    /// stesso motivo per cui il compito 8 ha reso verificabile <c>GetStorageDepthByAsiAsync</c>),
    /// quindi la risposta è una lista, non una cella sola. Una porta senza celle risponde una lista
    /// vuota, non un guasto.
    /// </summary>
    Task<IReadOnlyList<CellDescription>> DescribeCellsByDoorAsync(string doorCode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Imposta il blocco della cella la cui porta A è <paramref name="doorCode"/> (compito 9,
    /// ex <c>DbInfo.SetLockCell</c>). Solo la porta A: la scrittura originale filtrava solo su
    /// <c>CODICE_PORTA_A</c>, mai su <c>CODICE_PORTA_B</c> — migrato intatto, vedi il rapporto.
    /// Scrive tutti e quattro gli stati di <see cref="CellLockState"/>; resta da collaudare in campo
    /// - vedi <c>WCS/doc/debito-tecnico.md</c>, voce D113.
    /// </summary>
    Task SetCellLockStateAsync(string doorCode, CellLockState lockState, CancellationToken cancellationToken = default);

    /// <summary>
    /// Le unità di carico che occupano questa cella, nelle sole posizioni non assegnate, ordinate
    /// per posizione (compito 9, ex <c>DbInfo.GetLUsInCell</c>). Una cella vuota risponde una lista
    /// vuota, non un guasto.
    /// </summary>
    Task<IReadOnlyList<decimal>> GetCellContentAsync(decimal cellId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Le aree di prelievo servite da questa cella polmone, <b>in ordine di priorità</b> (compito 2,
    /// ex <c>DbInfo.PickingBufferAreas</c>: lettura diretta di <c>AREE_PRELIEVO_PICKING</c>).
    /// L'ordine è il dato, non un contorno: chi la chiama gira la lista a
    /// <see cref="FindLoadUnitRequest.AllowedAreaIds"/>, dove la prima area che risponde vince.
    /// <para>
    /// Una cella che non serve nessuna area risponde una lista <b>vuota</b> - è un dato, e chi la
    /// riceve ne trae una conseguenza vera (la missione non ha dove andare); un gestionale che non
    /// risponde solleva <see cref="WarehouseUnavailableException"/>. Erano la stessa cosa:
    /// <c>PickingBufferAreas</c> distingueva le due con un <see langword="null"/> che tre chiamanti
    /// su tre dovevano ricordarsi di guardare.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<decimal>> GetPickingBufferAreasAsync(decimal cellId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tutte le posizioni della cella, ordinate per posizione, con l'indicazione di quali siano
    /// prenotate (compito 18, ex <c>OperazioniUdc.UdcCella</c> con la porta A). Una cella vuota
    /// risponde una lista vuota, non un guasto.
    /// <para>
    /// <b>Non è <see cref="GetCellContentAsync"/> con un campo in più.</b> Quella risponde le sole
    /// posizioni non prenotate, e i suoi cinque chiamanti contano su quello scarto: allargarla
    /// cambierebbe la risposta sotto di loro. Chiave e forma sono le stesse, la semantica no, e due
    /// punti d'accesso stanno nella stessa operazione solo se condividono tutt'e tre.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<CellPosition>> GetCellPositionsAsync(decimal cellId, CancellationToken cancellationToken = default);

    /// <summary>
    /// La cella non ha più posizioni (compito 18, ex <c>OperazioniUdc.DissociaDaOperatore</c>):
    /// cancella <b>tutte</b> le righe di <c>POSIZIONI_UDC</c> di questa cella, prenotate e fisiche.
    /// <para>
    /// La firma è piccola e la semantica grossa: è la sola operazione di questo confine che cancella
    /// righe che non sono sue - non le posizioni di un'unità di carico che il motore sta muovendo, ma
    /// tutto quello che risulta in quella cella, comprese le prenotazioni che qualcun altro ci ha
    /// messo. Il nome storico diceva un'altra cosa (dissociare da un operatore), e non è stato
    /// portato di qua. Chi la chiama ha già verificato che nessuna delle unità di carico prenotate
    /// sia in missione: quella verifica la può fare solo il motore, ed è sua.
    /// </para>
    /// </summary>
    Task ClearCellPositionsAsync(decimal cellId, CancellationToken cancellationToken = default);

    /// <summary>
    /// L'occupazione della cella, in tre stati (compito 9): non è un booleano, vedi
    /// <see cref="CellBusyState"/>. <paramref name="loadUnitIdToExclude"/> esclude quell'unità di
    /// carico dal conteggio di prelievo e deposito — non ha effetto sullo stato di estrazione, che il
    /// gestionale calcola senza filtro per unità di carico (comportamento preesistente, migrato
    /// intatto: <c>IsCellBusyByExtraction</c> non aveva questo parametro).
    /// </summary>
    Task<CellBusyState> GetCellBusyStateAsync(decimal cellId, decimal loadUnitIdToExclude = 0, CancellationToken cancellationToken = default);

    /// <summary>
    /// Con quali vincoli si può depositare in questa cella (compito 9, ex <c>QuotaDepositoUdc</c>):
    /// la profondità teorica di deposito data la porta scelta, la profondità dell'unità di carico e
    /// l'unità di carico da escludere dal calcolo.
    /// </summary>
    Task<CellStoringConstraints> GetCellStoringConstraintsAsync(decimal cellId, CellDoor door, int loadUnitDepth, decimal loadUnitIdToExclude, CancellationToken cancellationToken = default);

    /// <summary>
    /// Che cosa c'e' scritto su questa unita' di carico: codice, tipo, modalita' di movimentazione e
    /// tara (compito 10) - sostituisce tre letture sparse sulla stessa riga di <c>UDC</c>/<c>TIPO_UDC</c>.
    /// Null se <paramref name="loadUnitId"/> non esiste: e' una risposta di dominio per chi verifica
    /// l'esistenza, non un guasto.
    /// </summary>
    Task<LoadUnitDescription?> DescribeLoadUnitAsync(decimal loadUnitId, CancellationToken cancellationToken = default);

    /// <summary>
    /// La modalita' di movimentazione del tipo di UDC. <see langword="null"/> se il tipo non esiste.
    /// Sostituisce <c>DbInfo.GetHandlingMode</c> (lettura diretta di <c>TIPO_UDC</c>).
    /// </summary>
    Task<LoadUnitTypeHandlingMode?> GetLoadUnitTypeHandlingModeAsync(decimal loadUnitTypeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Questa unita' di carico e' assegnata a una nota di spedizione (compito 3, ex
    /// <c>DbInfo.IsLuPresentInAssignmentNote</c>: lettura diretta di <c>ASSEGNAZIONE_NOTE</c>, che
    /// dal compito 21 e' tabella del gestionale)? Chi la chiama ne trae un rifiuto: l'unita' di
    /// carico e' impegnata da una nota e non si sposta a mano.
    /// <para>
    /// Lettura <b>severa</b>: la domanda ha sempre una risposta, anche su un'unita' di carico che
    /// non esiste - <see cref="LoadUnitAssignmentState.AssignedToNote"/> falso - quindi non c'e'
    /// nessun «non c'e'» da tradurre, e un gestionale che non risponde solleva
    /// <see cref="WarehouseUnavailableException"/>. Era proprio qui il difetto:
    /// <c>IsLuPresentInAssignmentNote</c> inghiottiva l'errore della lettura e rispondeva falso,
    /// cioe' «libera», lasciando partire lo spostamento a mano di un'unita' di carico che una nota
    /// stava aspettando.
    /// </para>
    /// </summary>
    Task<LoadUnitAssignmentState> GetLoadUnitAssignmentStateAsync(decimal loadUnitId, CancellationToken cancellationToken = default);

    /// <summary>
    /// L'unita' di carico con questo codice e' gia' a magazzino (compito 10, ex <c>DbInfo.IsLuIntoWarehouse</c>)?
    /// Un codice vuoto o senza posizione risponde falso.
    /// </summary>
    /// <remarks>
    /// <b>Il codice vuoto lo risolve chi chiama</b>, e non attraversa il confine: il codice e' un
    /// segmento del percorso, e un segmento vuoto non lega un parametro obbligatorio — la richiesta
    /// non corrisponderebbe a nessuna rotta e il 404 di instradamento diventerebbe un guasto.
    /// <para>
    /// <b>Non «come rispondeva la query originale»,</b> che e' quello che diceva qui: la query
    /// originale nominava una colonna che <c>POSIZIONI_UDC</c> non ha e falliva sempre,
    /// rispondendo falso solo perche' l'errore veniva inghiottito. Da adesso la risposta e' il
    /// vero, e un'unita' che risulta gia' a magazzino non entra.
    /// </para>
    /// </remarks>
    Task<bool> IsLoadUnitStoredAsync(string? code, CancellationToken cancellationToken = default);

    /// <summary>
    /// L'identificativo dell'unita' di carico con questo codice (compito 10, ex <c>DbInfo.GetLuId</c>).
    /// Null se nessuna unita' di carico ha quel codice: e' una risposta di dominio, non un guasto.
    /// </summary>
    Task<LoadUnitIdentity?> ResolveLoadUnitByCodeAsync(string? code, CancellationToken cancellationToken = default);

    /// <summary>
    /// Quest'area va deframmentata per questo tipo di unita' di carico (compito 4, ex
    /// <c>DbInfo.IsCellToDefragmentByLuType</c>: lettura diretta di <c>CELLE_AREE</c>,
    /// <c>POSIZIONI_UDC</c> e <c>AREE_MAGAZZINO</c>)? Chi la chiama ne trae una cosa sola: la cella
    /// appena riempita e' candidata alla compattazione.
    /// <para>
    /// <b>La politica resta del gestionale</b>, e non attraversa il confine. Il motore riceve si' o
    /// no; le soglie - oggi piu' dell'85 % di celle occupate, oppure piu' del 10 % con una sola
    /// unita' di carico - e i tre conteggi da cui si ricavano stanno di la'. Prima stavano di qua,
    /// scritte in un <c>switch</c> dentro la funzione di lettura del motore.
    /// </para>
    /// <para>
    /// Lettura <b>severa</b>: la domanda ha sempre una risposta - un'area senza celle di quel tipo
    /// non va deframmentata, e questo e' un dato - quindi non c'e' nessun «non c'e'» da tradurre, e
    /// un gestionale che non risponde solleva <see cref="WarehouseUnavailableException"/>. Era
    /// proprio qui il difetto: <c>IsCellToDefragmentByLuType</c> inghiottiva l'errore della lettura,
    /// lo scriveva nel registro e rispondeva falso, cioe' «l'area e' a posto».
    /// </para>
    /// </summary>
    Task<AreaDefragmentationVerdict> GetAreaDefragmentationVerdictAsync(decimal areaId, decimal loadUnitTypeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Le celle da mostrare nell'Editor quando si collegano porte e polmoni all'impianto (compito
    /// 7, ex <c>WmsQueries.CellsAsync</c>: lettura diretta di <c>CELLE_AREE</c>,
    /// <c>AREE_MAGAZZINO</c> e <c>MAGAZZINI</c> fatta dall'host sulla connessione al WMS del
    /// motore). Ordinate per magazzino, area, colonna e piano: e' un elenco da scegliere, e
    /// l'ordine e' quello con cui si legge.
    /// </summary>
    /// <param name="areaType">
    /// Il tipo d'area da mostrare, oppure <see langword="null"/> per le aree di stoccaggio (tipo 0
    /// e 1): sono le due domande che le due finestre dell'Editor facevano, unificate.
    /// </param>
    /// <param name="automaticOnly">Solo le aree automatiche, come chiede la finestra dei collegamenti dei dispositivi.</param>
    /// <returns>
    /// Le celle, eventualmente nessuna: un impianto senza aree di quel tipo risponde una lista
    /// vuota, che e' un dato - la finestra si apre vuota. Un gestionale che non risponde solleva
    /// <see cref="WarehouseUnavailableException"/>, e l'host la rende <c>503</c>: l'Editor dice
    /// che non si puo' leggere, invece di mostrare un elenco vuoto che sembra un impianto da
    /// configurare.
    /// </returns>
    /// <remarks>
    /// E' una lettura <b>guidata dall'Editor</b>: parte all'apertura di una finestra, non nel giro
    /// periodico del motore. Il motore non la chiama mai - non gli serve nessuno di quei campi -
    /// ed e' l'host a girarla al confine per conto dell'Editor.
    /// </remarks>
    Task<IReadOnlyList<EditorCellRow>> GetCellsForEditorAsync(short? areaType, bool automaticOnly, CancellationToken cancellationToken = default);

    /// <summary>
    /// Il controllo di configurazione dell'impianto visto dal gestionale (compito 7, ex
    /// <c>WmsQueries.ConfigurationCheckAsync</c>): le cose che non tornano fra magazzini, aree e
    /// celle, una riga per ciascuna. La regola di che cosa non torna e' del gestionale
    /// (<c>dbo.sp_check_configurazione</c>) e non attraversa il confine: il WCS riceve i messaggi
    /// gia' scritti e li mostra.
    /// </summary>
    /// <param name="mode">
    /// La modalita' del controllo, come la chiede l'Editor (oggi sempre zero). Viaggia perche' e'
    /// il parametro della procedura: quale controllo faccia e' affare del gestionale.
    /// </param>
    /// <returns>
    /// Le righe di segnalazione, eventualmente nessuna: <b>nessuna riga vuol dire configurazione a
    /// posto</b>, ed e' un dato. Un gestionale che non risponde solleva
    /// <see cref="WarehouseUnavailableException"/>, che l'host rende <c>503</c>: una lista vuota
    /// al posto di un guasto direbbe all'Editor che l'impianto e' configurato bene.
    /// </returns>
    /// <remarks>Come <see cref="GetCellsForEditorAsync"/>, e' una lettura guidata dall'Editor: parte quando si apre la finestra del controllo.</remarks>
    Task<IReadOnlyList<ConfigurationWarning>> GetConfigurationWarningsAsync(int mode, CancellationToken cancellationToken = default);
}
