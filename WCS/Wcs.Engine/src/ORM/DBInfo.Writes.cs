using Helper.Converters;
using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Log.Interface;
using System.Data;
using System.Reflection.PortableExecutable;
using Wcs.Connection.Interface;
using Wcs.Wms.Contracts;
using static Wcs.Common.Enums;
using static Wcs.Engine.Navigator;

namespace Wcs.Engine.ORM;

internal static partial class DbInfo
{

    #region Methods

    /// <summary>
    /// <b>La missione che se ne va nomina anche l'assegnazione che la sua riga di coda portava</b>
    /// (compito 13). Era <c>DeleteAssignedLuByIdEstrazioneUdc</c>, che leggeva l'assegnazione dalla
    /// riga di coda del WCS e la cancellava da <c>ASSEGNAZIONE_NOTE</c> con un <c>DELETE</c> in
    /// T-SQL diretto: l'ultima scrittura del motore su quella tabella.
    /// </summary>
    /// <remarks>
    /// <b>Non e' un doppione della segnalazione che la precede nei quattro punti.</b> Quella parte solo
    /// quando la missione porta il <c>GUID_ASSEGNAZIONE_UDC</c> e nomina l'assegnazione
    /// <b>intera</b> dell'unita' di carico; questa nomina la <b>riga</b> che la coda indicava, che
    /// e' esattamente quella che il <c>DELETE</c> toglieva. Sono due campi dello stesso fatto - la
    /// missione se n'e' andata senza servire - e il contratto li ammette tutt'e due.
    /// <para>
    /// I chiamanti sono tre dei quattro punti che <see cref="IWarehouse.ReportMissionLeftAssignmentUnservedAsync"/>
    /// dichiara: <c>ActiveMissions.Remove</c>, la missione di compattazione che non trova
    /// l'unita' di carico, e la missione ricaricata all'avvio che risulta gia' a destinazione. Il
    /// quarto, dalla voce 39 il cambio cella di un'estrazione (<c>MissionGateway.LeaveTheExtractionAsync</c>),
    /// manda la stessa richiesta da se': deve sapere quale delle sue segnalazioni e' la prima a
    /// partire (voce 39, secondo giro, compito 9).
    /// </para>
    /// <para>
    /// <b>Un guasto del confine risale</b>, e da qui in poi la riga di coda non viene cancellata:
    /// i chiamanti hanno il proprio <c>try/catch</c>, che registra e lascia la riga dov'e'. Prima
    /// l'errore SQL veniva inghiottito qui dentro e il giro proseguiva.
    /// </para>
    /// </remarks>
    internal static async Task ReportQueuedAssignmentLeftUnserved(Guid missionId, decimal loadUnitId,
        decimal idEstrazioneUdc, string reason)
    {
        var assignmentId = await AssignmentOfQueueRow(idEstrazioneUdc);
        if (assignmentId is not > 0) return;

        await Navigator.RequireWarehouse(nameof(ReportQueuedAssignmentLeftUnserved))
            .ReportMissionLeftAssignmentUnservedAsync(new MissionLeftAssignmentUnservedRequest(
                missionId, loadUnitId, Guid.Empty, assignmentId.Value, reason));
    }

    /// <summary>
    /// <b>Il motore scarta l'assegnazione di nota che non puo' servire</b> (compito 13), e dice
    /// perche'. Chiave, forma e conseguenza sono quelle del <c>DELETE</c> che il motore faceva da
    /// se': una riga sola di <c>ASSEGNAZIONE_NOTE</c>, quella nominata da
    /// <c>ID_ASSEGNAZIONE_UDC</c>.
    /// </summary>
    /// <remarks>
    /// <b>Perche' non <see cref="IWarehouse.ReportUnservableQueuedExtractionAsync"/></b>, che
    /// semanticamente sarebbe la piu' vicina: quella nomina l'assegnazione con il GUID, cioe'
    /// cancella <b>tutte</b> le righe nate dallo stesso giro di assegnazione dell'unita' di carico.
    /// Sarebbe un comportamento diverso, non una migrazione. E <b>non</b> il fatto della missione
    /// non servita, perche' in questi punti nessuna missione se ne va: se ne va una riga di coda.
    /// <para>
    /// I chiamanti sono quattro, e nessuno di loro e' quello che il compito 14 aveva gia'
    /// (<c>Navigator</c>, il quarto motivo di rifiuto): i due del ciclo di sfondo del magazzino, e
    /// i due delle pulizie periodiche della coda.
    /// </para>
    /// </remarks>
    internal static async Task RejectNoteAssignment(decimal assignmentId, decimal loadUnitId, string reason)
    {
        if (assignmentId == 0) return;

        await Navigator.RequireWarehouse(nameof(RejectNoteAssignment))
            .RejectNoteAssignmentAsync(new RejectNoteAssignmentRequest(assignmentId, loadUnitId, reason));
    }

    /// <summary>L'assegnazione che una riga della coda di estrazione del WCS sta servendo, o <c>null</c>.</summary>
    internal static async Task<decimal?> AssignmentOfQueueRow(decimal idEstrazioneUdc)
    {
        if (idEstrazioneUdc == 0) return null;

        return await ExtractionQueue.ReadAsync(
            async context => (await Persistence.ExtractionQueueStore.FindAsync(context, (long)idEstrazioneUdc))?.AssignmentId, null);
    }

    internal static async Task DeleteExtractionItemQueueById(decimal extractionId)
    {
        if (extractionId == 0)
            return;

        await ExtractionQueue.WriteAsync(context => Persistence.ExtractionQueueStore.DeleteAsync(context, (long)extractionId));
    }

    internal static async Task DeleteLuFromExtractionQueue(decimal idLu)
    {
        var idAssegnazioneNota = await GetIdAssegnazioneUdc(idLu);
        var idExtractionQueue = await GetIdEstrazioneUdc(idLu);
        var rowIdExtractionQueue = await GetExtractionLuRowId(idLu);

        // Compito 12: per quella riga di estrazione non arrivera' piu' nessuno, e il fatto si segnala.
        //
        // Sta QUI, prima di qualunque cancellazione, e non in fondo al metodo: piu' sotto la riga di
        // coda viene cancellata, e da quel punto in poi l'identificativo della riga di estrazione non
        // e' piu' ricavabile da nessuna parte. Se un passo intermedio fallisse - la cancellazione
        // dell'assegnazione e' SQL diretto, e puo' - l'esito si perderebbe per sempre e il comando
        // manuale resterebbe a meta'. L'identificativo lo si e' appena letto, quindi niente lo
        // impedisce, e la segnalazione e' idempotente grazie all'upsert dello stesso compito, quindi
        // anticiparla non costa niente.
        //
        // Qui il comportamento in campo CAMBIA, perche' prima non succedeva niente: la chiamata era
        // UpdateStatusExtractionLu(rowIdExtractionQueue, ...), ma rowIdExtractionQueue viene da
        // GetExtractionLuRowId, che risponde ExtractionQueueItem.ExtractionRowId - la riga del
        // gestionale - mentre UpdateStatusExtractionLu la cercava in coda come ExtractionQueueItem.Id,
        // la chiave del WCS. Due identificativi diversi, e la lettura non trovava niente; e anche con
        // la chiave giusta la riga di coda era gia' stata cancellata qualche riga sopra. Adesso
        // l'identificativo che questo metodo ha gia' in mano e' proprio quello che la segnalazione
        // vuole.
        await ReportExtractionRowOutcomeById(rowIdExtractionQueue, ExtractionRowStatus.NotReachable);

        // La segnalazione, non due ordini. Il motore dice il fatto che possiede - questa unita' di
        // carico ha lasciato la coda di estrazione, e la riga che se ne va stava servendo (o no)
        // quell'assegnazione - e le conseguenze le sceglie il gestionale. Prima erano due chiamate
        // in sequenza che gli dicevano che cosa scrivere: togli la posizione prenotata dell'unita'
        // di carico, e rimetti «non assegnata» la nota di quell'assegnazione. Per formularle il
        // motore doveva conoscere il modello di dati dell'altro; adesso non piu', e il giorno in
        // cui una conseguenza cambia, cambia da una parte sola.
        //
        // L'assegnazione viaggia sempre, zero compreso: non e' la chiave del fatto, e' un suo
        // campo, e il «se c'e' un'assegnazione» che qui era un if e' diventato una decisione di la'.
        //
        // Compito 21: la cancellazione delle righe di ASSEGNAZIONE_NOTE che il motore faceva subito
        // dopo questa segnalazione non e' un settimo fatto - e' la TERZA CONSEGUENZA della stessa,
        // e adesso la trae il gestionale insieme alle altre due. Qui non e' rimasto niente da
        // chiedergli: e' l'unico dei sei punti del compito che non ha prodotto un'operazione nuova.
        await Navigator.RequireWarehouse("DeleteLuFromExtractionQueue")
            .ReportLoadUnitLeftExtractionQueueAsync(new LoadUnitLeftExtractionQueueRequest(idLu, idAssegnazioneNota));

        if (idAssegnazioneNota > 0 && idLu > 0)
            WriteLog(
                $"DEBUG INFO: Cancellazione ASSEGNAZIONE_NOTE da DeleteUDCFromExtractionQueue->IdAssegnazioneNota UDC: {idLu} - {(await Navigator.RequireWarehouse("DeleteLuFromExtractionQueue").DescribeLoadUnitAsync(idLu))?.Code} Date: {DateTime.Now:dd/MM/yyyy HH:mm:ss}",
                LogEntryType.Information);

        if (idExtractionQueue > 0)
        {
            if (idLu > 0)
            {
                var loadUnitCode = (await Navigator.RequireWarehouse("DeleteLuFromExtractionQueue").DescribeLoadUnitAsync(idLu))?.Code;
                WriteLog(
                    $"DEBUG INFO: Cancellazione ASSEGNAZIONE_NOTE da DeleteUDCFromExtractionQueue->IdExtractionQueue UDC: {idLu} - {loadUnitCode} Date: {DateTime.Now:dd/MM/yyyy HH:mm:ss}",
                    LogEntryType.Information);
                WriteLog(
                    $"DEBUG INFO: Cancellazione ESTRAZIONI_UDC_AUTOMATICHE da DeleteUDCFromExtractionQueue->IdExtractionQueue UDC: {idLu} - {loadUnitCode} Date: {DateTime.Now:dd/MM/yyyy HH:mm:ss}",
                    LogEntryType.Information);
            }

            // Compito 13: qui c'era DeleteAssignedLuByIdEstrazioneUdc, e non e' stata sostituita da
            // niente perche' era RIDONDANTE. idAssegnazioneNota e idExtractionQueue vengono tutt'e
            // due dalla stessa riga di coda (GetIdAssegnazioneUdc e GetIdEstrazioneUdc leggono
            // ExtractionQueueStore.FindByLoadUnitAsync sulla stessa unita' di carico), e
            // l'assegnazione e' gia' viaggiata nella segnalazione qui sopra, la cui terza
            // conseguenza dal lato del gestionale e' proprio la cancellazione di quella riga
            // (compito 21). Cancellarla una seconda volta in T-SQL diretto non toglieva niente di
            // piu'.
            await DeleteExtractionItemQueueById(idExtractionQueue);
        }

        foreach (var item in PathDevices.Instance.Where(x => x != null && x.QueuedLuToPickUpCount != 0))
        {
            if (item == null) continue;

            // La coda restituisce una copia: l'indice serve solo a scegliere l'elemento, e la
            // rimozione avviene per riferimento sotto il lock della coda.
            var queue = item.QueuedLUtoPickUp;
            var index = queue.FindIndex(x => x != null && x.LuId == idLu);

            // ">= 0" e non "> 0": FindIndex restituisce 0 quando la UDC cercata e' la PRIMA della
            // coda, e con "> 0" quel caso non veniva mai tolto -- proprio il piu' probabile, perche'
            // la testa della coda e' la prossima che il dispositivo prova a prelevare. Voce D71.
            if (index >= 0)
                item.RemoveLuToPickUp(queue[index]);
        }
    }

    /// <summary>
    /// Toglie dalla coda le righe delle estrazioni per UDC la cui lavorazione non e' piu' in corso.
    /// Le righe di estrazione sospese si leggono dal WMS, la coda dal database del WCS: prima era
    /// un JOIN, e la callback per riga era <c>async void</c>, non attesa.
    /// </summary>
    /// <remarks>
    /// Dal compito 15 fa una cosa sola. La pulizia delle distinte delle lavorazioni sospese, che
    /// girava qui in testa (<c>DeleteDISTINTA_ESTRAZIONI_UDC</c>), era una scrittura sui dati del
    /// gestionale ed e' passata di la', in testa a <c>IWarehouse.GetPendingExtractionWorkAsync</c>,
    /// che gira nello stesso giro del motore. Quello che resta - la coda - vive nel database del
    /// WCS, che il gestionale non vede, ed e' per questo che resta qui come la gemella delle note.
    /// <para>
    /// Dal compito 5 la meta' che guardava le lavorazioni non e' piu' una query di qua:
    /// <see cref="IWarehouse.GetPausedExtractionRowsAsync"/> risponde quali delle righe date sono
    /// in pausa, e con lei se ne va anche lo spezzettamento in blocchi da mille parametri - il
    /// limite e' di chi esegue la query, cioe' del gestionale. <b>Cambia il modo di rompersi</b>:
    /// <c>PausedAmongAsync</c> registrava l'errore e rispondeva <see langword="null"/>, che qui
    /// diventava «nessuna in pausa» e faceva saltare la pulizia in silenzio; adesso un gestionale
    /// che non risponde solleva, e a raccogliere e' il <c>catch</c> per giro di
    /// <c>Navigator.AssignmentsProcess</c> - lo stesso che copre gia' la
    /// <c>GetPendingExtractionWorkAsync</c> di due righe piu' sotto.
    /// </para>
    /// </remarks>
    internal static async Task DeleteLuInExtranctionByPausedExtractionLu()
    {
        // Si parte dalla coda, che ha poche righe, e si chiede al WMS solo di quelle. Prima si
        // leggevano tutte le righe di estrazione sospese del WMS (422.317 sull'impianto di prova,
        // a ogni ciclo) per passarle a SQLite in un IN: «too many SQL variables», e il processo
        // delle assegnazioni cadeva a ogni giro.
        var queued = await ExtractionQueue.ReadAsync(Persistence.ExtractionQueueStore.WithExtractionRowAsync,
            new List<Persistence.Entities.ExtractionQueueItem>());

        if (queued.Count == 0) return;

        var paused = (await Navigator.RequireWarehouse(nameof(DeleteLuInExtranctionByPausedExtractionLu))
            .GetPausedExtractionRowsAsync(queued.Select(r => r.ExtractionRowId!.Value).Distinct().ToList()))
            .ToHashSet();

        if (paused.Count == 0) return;

        foreach (var row in queued.Where(r => paused.Contains(r.ExtractionRowId!.Value)))
        {
            if (row.LoadUnitId > 0)
            {
                var loadUnitCode = (await Navigator.RequireWarehouse("DeleteLuInExtranctionByPausedExtractionLu").DescribeLoadUnitAsync(row.LoadUnitId))?.Code;
                WriteLog(
                    $"DEBUG INFO: Cancellazione ASSEGNAZIONE_NOTE da DeleteESTRAZIONI_LAVORAZIONI_ESTRAZIONI_UDC da BuildRouteToDestination IdExtractionQueue UDC: {row.LoadUnitId} - {loadUnitCode} Date: {DateTime.Now:dd/MM/yyyy HH:mm:ss}",
                    LogEntryType.Information);
                WriteLog(
                    $"DEBUG INFO: Cancellazione ESTRAZIONI_UDC_AUTOMATICHE da DeleteESTRAZIONI_LAVORAZIONI_ESTRAZIONI_UDC UDC: {row.LoadUnitId} - {loadUnitCode} Date: {DateTime.Now:dd/MM/yyyy HH:mm:ss}",
                    LogEntryType.Information);
            }

            // Compito 13: il fatto al posto del DELETE su ASSEGNAZIONE_NOTE. Una riga di estrazione
            // per unita' di carico di norma un'assegnazione di nota non ce l'ha - i due campi si
            // escludono - ma la guardia resta perche' la coda ammette la combinazione, ed era
            // esattamente quello che il metodo cancellato controllava prima di scrivere.
            if (row.AssignmentId is > 0)
                await RejectNoteAssignment(row.AssignmentId.Value, row.LoadUnitId,
                    "la lavorazione di estrazione della riga in coda non e' piu' in corso");

            await DeleteExtractionItemQueueById(row.Id);
        }
    }

    /// <summary>
    /// Toglie dalla coda le righe delle note di spedizione la cui lavorazione non e' piu' in corso.
    /// Le assegnazioni sospese si chiedono al gestionale, la coda si legge dal database del WCS.
    /// </summary>
    /// <remarks>
    /// Dal compito 6 la meta' che guardava le lavorazioni non e' piu' una query di qua:
    /// <see cref="IWarehouse.GetPausedNoteAssignmentsAsync"/> risponde quali delle assegnazioni
    /// date sono in pausa, e con lei se ne va anche lo spezzettamento in blocchi da mille
    /// parametri - il limite e' di chi esegue la query, cioe' del gestionale. <b>Cambia il modo di
    /// rompersi</b>: <c>PausedAmongAsync</c> registrava l'errore e rispondeva
    /// <see langword="null"/>, che qui diventava «nessuna in pausa» e faceva saltare la pulizia in
    /// silenzio; adesso un gestionale che non risponde solleva, e a raccogliere e' il <c>catch</c>
    /// per giro di <c>Navigator.AssignmentsProcess</c>, lo stesso della gemella delle estrazioni.
    /// <para>
    /// <b>E' l'unico posto da cui quella domanda parte</b>, dalla revisione del compito 6. Prima la
    /// poneva anche <c>DeleteQueueLuOfNoteDone</c>, la prima parte di <see cref="CheckOperation"/>,
    /// nello stesso giro di <c>MasterProcess</c> e con la stessa chiave: due richieste identiche al
    /// secondo, e la seconda non poteva nemmeno piu' cancellare niente, perche' quando le arrivava
    /// il turno le righe in pausa erano gia' uscite di qui. E' stata cancellata.
    /// </para>
    /// <para>
    /// <b>Che cosa esce dalla coda: la riga, non l'unita' di carico.</b> Si toglie la sola riga che
    /// portava l'assegnazione in pausa, come fa la gemella delle estrazioni. La pulizia cancellata
    /// toglieva invece <b>tutte</b> le righe di quella unita' di carico, comprese quelle senza
    /// assegnazione (i ricollocamenti) e quelle che ne servono un'altra ancora viva. Quale delle
    /// due si voglia in campo per le righe <b>senza</b> assegnazione della stessa unita' di carico
    /// e' una domanda aperta per chi possiede il comportamento: finche' non ha risposta, qui resta
    /// la forma per riga, che e' quella che non cancella lavoro vivo.
    /// </para>
    /// </remarks>
    internal static async Task DeleteLuInExtranctionByPausedNote()
    {
        // Stessa forma del metodo sopra: prima la coda, poi il WMS solo sulle sue assegnazioni.
        var queued = await ExtractionQueue.ReadAsync(Persistence.ExtractionQueueStore.WithAssignmentAsync,
            new List<Persistence.Entities.ExtractionQueueItem>());

        if (queued.Count == 0) return;

        var paused = (await Navigator.RequireWarehouse(nameof(DeleteLuInExtranctionByPausedNote))
            .GetPausedNoteAssignmentsAsync(queued.Select(r => r.AssignmentId!.Value).Distinct().ToList()))
            .ToHashSet();

        if (paused.Count == 0) return;

        foreach (var row in queued.Where(r => paused.Contains(r.AssignmentId!.Value)))
        {
            if (row.LoadUnitId > 0)
            {
                var loadUnitCode = (await Navigator.RequireWarehouse(nameof(DeleteLuInExtranctionByPausedNote)).DescribeLoadUnitAsync(row.LoadUnitId))?.Code;
                WriteLog(
                    $"DEBUG INFO: Cancellazione ASSEGNAZIONE_NOTE da DeleteESTRAZIONI_LAVORAZIONI_NOTE da BuildRouteToDestination IdExtractionQueue UDC: {row.LoadUnitId} - {loadUnitCode} Date: {DateTime.Now:dd/MM/yyyy HH:mm:ss}",
                    LogEntryType.Information);
                WriteLog(
                    $"DEBUG INFO: Cancellazione ESTRAZIONI_UDC_AUTOMATICHE da DeleteESTRAZIONI_LAVORAZIONI_NOTE UDC: {row.LoadUnitId} - {loadUnitCode} Date: {DateTime.Now:dd/MM/yyyy HH:mm:ss}",
                    LogEntryType.Information);
            }

            // Compito 13: il fatto al posto del DELETE su ASSEGNAZIONE_NOTE. Qui l'assegnazione c'e'
            // per costruzione - la selezione della coda e' WithAssignmentAsync - e la si ha gia' in
            // mano, quindi non serve rileggere la riga. La segnalazione viene prima della
            // cancellazione della riga di coda: se il confine non risponde, l'eccezione risale al
            // catch per giro di Navigator.AssignmentsProcess e la riga resta da ripulire al giro
            // dopo, invece di sparire lasciando viva l'assegnazione di una nota ferma.
            await RejectNoteAssignment(row.AssignmentId!.Value, row.LoadUnitId,
                "la lavorazione della nota assegnata non e' piu' in corso");

            await DeleteExtractionItemQueueById(row.Id);
        }
    }

    internal static async Task RecoverFailure()
    {
        // Gli elenchi che sopravvivono al riavvio (risposte 2 e 3 del proprietario del 9/10): le
        // prenotazioni delle missioni chiuse alla ricarica, da restituire al passaggio a Run, e le celle
        // da segnare piene dopo un 67 con il gestionale muto, da ritentare al giro del freno. Prima
        // dell'uscita per l'impianto vuoto: non dipendono dai dispositivi. Letture del database del WCS,
        // niente gestionale.
        await ReservationsOfMissionsClosedOnReload.LoadAsync();
        await CellsToMarkFull.LoadAsync();

        if (PathDevices.Instance.Count == 0)
            return;

        foreach (var item in PathDevices.Instance.List)
        {
            item.ClearDevicesReadyToTransferLu();
            item.ClearLuToPickUp();

            // Rete di sicurezza per le prenotazioni della voce D73. La ricarica ricostruisce lo
            // stato di esecuzione da zero ed e' un ritentativo a tutti gli effetti, ma PathDevices
            // NON si svuota (voci B1 e B12): i dispositivi sopravvivono con addosso il gettone di
            // uno step che non esiste piu', e resterebbero inaccettabili per chiunque. Qui il posto
            // si restituisce, e chi rilancia il movimento se lo riprende.
            //
            // E' una revoca, non una rinuncia: il posto si toglie a chiunque lo tenga, senza
            // sapere chi sia. Qui e' legittimo perche' gli step che avevano prenotato non esistono
            // piu' — la ricarica li ricostruisce come oggetti nuovi — quindi non c'e' nessun
            // mittente da avvisare.
            item.ForceReleaseIncomingStep();

            // Le missioni vengono ricostruite qui sotto come oggetti NUOVI, ma i dispositivi
            // sopravvivono alla ricarica (PathDevices non si svuota, voci B1 e B12) e tenevano
            // questi due riferimenti agli oggetti vecchi, che non stanno piu' in nessuna
            // collezione: un dispositivo poteva aspettare per sempre una UDC che il motore non
            // conosceva piu'. Le code e le iscrizioni erano gia' azzerate; questi due no.
            item.LUtoPick = null;
            item.LUtoReceive = null;
        }

        await LoadMissions(OperationTypes.Inbound, activeFilter: true);

        await LoadMissions(OperationTypes.Outbound, activeFilter: false);

        await LoadMissions(OperationTypes.Inbound, activeFilter: false);

        await LoadMissions(OperationTypes.Outbound, activeFilter: true);

        // ResetOperationMapItems()

        foreach (var item in PathDevices.Instance.Where(x =>
                     x is { IsLinkedToStorage: true, BelongedGroup: null }).OfType<Items.AutomationDevice>())
            await RecoverPickUpQueue(item);

        foreach (var item in PathDevices.Instance.List.OfType<Items.AutomationDevice>())
        {
            // Error e Warning tornano a Ready insieme agli stati transitori, ed e' voluto.
            //
            // Lo stato di errore di un dispositivo NON e' uno stato fisico: e' contabilita' del
            // motore, e significa "l'ultimo comando non e' stato eseguito". Le unita' fisiche non
            // tengono un errore latch da rispecchiare: quando falliscono comunicano solo
            // l'impossibilita' di eseguire quel comando. La via d'uscita prevista e' RIPROVARE — ed
            // e' esattamente cio' che fanno ClearError e ResetError, che riportano a Ready e
            // rilanciano ExecuteStep. Se nel frattempo il problema fisico e' rientrato, il movimento
            // riprende.
            //
            // Una ricarica ricostruisce lo stato di esecuzione da zero, quindi e' un ritentativo a
            // tutti gli effetti: lasciare Error qui dentro terrebbe il dispositivo fuori dai percorsi
            // (PathBuilder lo scarta) finche' un operatore non fa un reset esplicito, togliendo il
            // ritentativo automatico che il progetto prevede. Si veda la voce B23 del registro in
            // WCS/doc/debito-tecnico.md.
            item.Status = item.Status switch
            {
                DeviceStatusTypes.Loading or DeviceStatusTypes.Unloading or DeviceStatusTypes.Moving
                    or DeviceStatusTypes.Busy or DeviceStatusTypes.Recovering => DeviceStatusTypes.Ready,
                DeviceStatusTypes.Error or DeviceStatusTypes.Warning => DeviceStatusTypes.Ready,
                _ => item.Status
            };

            await AutomationDevice.UpdateStatus(item);
        }
    }

    internal static async Task SetCellLockedForStoring(string doorCode)
    {
        if (string.IsNullOrEmpty(doorCode))
            return;

        await ExtractionQueue.WriteAsync(context => Persistence.DoorMappingStore.SetStoringLockedAsync(context, doorCode, true));
    }

    /// <summary>
    /// Prova a prendere il blocco della porta con una sola operazione serializzata. Il risultato
    /// vero identifica il proprietario che, e soltanto lui, dovra' poi rilasciarlo.
    /// </summary>
    internal static async Task<bool> TrySetCellLockedForStoring(string doorCode)
    {
        if (string.IsNullOrEmpty(doorCode)) return false;

        var acquired = false;
        await ExtractionQueue.WriteAsync(async context =>
            acquired = await Persistence.DoorMappingStore.TrySetStoringLockedAsync(context, doorCode));
        return acquired;
    }

    internal static async Task SetCellUnlockedForStoring(string? doorCode)
    {
        if (string.IsNullOrEmpty(doorCode))
            return;

        await ExtractionQueue.WriteAsync(context => Persistence.DoorMappingStore.SetStoringLockedAsync(context, doorCode, false));
    }

    internal static async Task SetExtractionItemQueueProcessed(decimal rowId, int value)
    {
        if (rowId == 0)
            return;

        await ExtractionQueue.WriteAsync(context => Persistence.ExtractionQueueStore.SetProcessedAsync(context, (long)rowId, value != 0));
    }

    internal static async Task SetOperationLockingCell(decimal cellId, StatoOperazioneCella operationStatus)
    {
        if (cellId == 0)
            return;

        await ExtractionQueue.WriteAsync(context => Persistence.DoorMappingStore.SetOperationActiveAsync(context, cellId, (short)(byte)operationStatus));
    }

    internal static Task UpdateDefragmentationStatus(DefragmentationTypes value)
    {
        return Persistence.PlantPersistence.SaveCompactingStatusAsync((short)value);
    }

    /// <param name="processingId">La lavorazione della nuova riga di estrazione (compito 8): viaggia
    /// in coppia con <paramref name="rowId"/>, perche' la riga di coda non la rilegge piu' dal
    /// gestionale.</param>
    internal static async Task UpdateExtractionLu(decimal luId, decimal? bufferCellId, decimal rowId, decimal processingId = 0)
    {
        if (luId == 0 || !bufferCellId.HasValue ||
            bufferCellId == 0 || rowId == 0)
            return;

        var destinationItemId = await GetItemId(bufferCellId.Value);
        await ExtractionQueue.WriteAsync(context => Persistence.ExtractionQueueStore.RedirectByLoadUnitAsync(
            context, luId, destinationItemId == Guid.Empty ? null : destinationItemId, bufferCellId.Value, rowId, processingId));
    }

    /// <summary>
    /// Rinnova i contatori sulla riga del diagramma: quello di controllo a ogni giro, quello di
    /// stato solo quando lo stato e' cambiato. Chi legge la riga da fuori vede che il motore e' vivo.
    /// </summary>
    internal static Task UpdateNavigatorStatusCounters(bool updateStatus)
    {
        // Larghezza fissa, non un numero a caso. rnd.Next(1, 9999) cambiava larghezza ogni volta
        // che il valore attraversava 127 (un byte contro due), circa nel 2,5 % dei secondi: a ogni
        // cambio di larghezza SQLite riscrive il record intero, e questo record porta DiagramXml -
        // 36 MB nella -wal, una volta ogni ~40 secondi. Un intero >= 2^30 occupa sempre quattro
        // byte, quindi il record non cambia lunghezza e SQLite riscrive una pagina sola.
        var beat = unchecked((int)(Environment.TickCount64 & 0x7FFFFFFF)) | 0x40000000;
        return Persistence.PlantPersistence.SaveCountersAsync(beat, updateStatus ? beat : null);
    }

    /// <summary>
    /// <b>Segnala</b> al gestionale che cosa è successo alla riga di estrazione servita da una riga
    /// della coda del WCS (compito 12, ex <c>UpdateStatusExtractionLu</c>, che quell'esito lo
    /// scriveva da sé su <c>DISTINTA_ESTRAZIONI_UDC</c> e poi chiudeva la lavorazione). La riga di
    /// estrazione si legge dalla coda, che è nel database del WCS; la conseguenza - lo stato sulla
    /// distinta, e la chiusura della lavorazione quando il residuo va a zero - la trae il gestionale
    /// (compito 11).
    /// </summary>
    /// <remarks>
    /// <b>La testata viaggia a zero, e non è un dato inventato:</b> chi arriva qui conosce la
    /// <i>riga</i> di estrazione, non la testata, e il gestionale ricava la lavorazione dalle righe
    /// segnalate (<c>LavorazioniDelleRigheSql</c>, compito 11). Lo zero è già oggi «non c'è» dall'altra
    /// parte, e non tocca il database.
    /// <para>
    /// <b>Una riga di coda senza riga di estrazione non ha niente da segnalare</b>, ed è la stessa
    /// guardia di prima (l'<c>UPDATE</c> partiva solo con <c>ExtractionRowId &gt; 0</c>): sono le
    /// righe nate da un'assegnazione di nota o da un ricollocamento, che sulla distinta del
    /// gestionale non hanno una riga propria. Il confine non viene nemmeno toccato.
    /// </para>
    /// <para>
    /// <b>Il guasto del confine risale.</b> Qui non c'è lo stato degradato che
    /// <c>Navigator.ServeExtraction</c> può permettersi riga per riga, perché un esito perso in
    /// silenzio lascerebbe la lavorazione aperta per sempre.
    /// </para>
    /// <para>
    /// <b>Un guasto del confine resta visibile ai chiamanti di questo helper.</b>
    /// <see cref="DeleteLuFromExtractionQueue"/> è un comando manuale, che nessun ciclo ripete:
    /// la segnalazione sta quindi in testa al metodo, prima di qualunque cancellazione, così un
    /// guasto lascia il comando intero da rifare. La missione che completa un'estrazione non usa
    /// più questo percorso diretto: registra <c>Extracted</c> nell'outbox locale e il dispatcher
    /// ne gestisce la consegna durevole.
    /// </para>
    /// </remarks>
    internal static async Task ReportExtractionRowOutcome(decimal rowId, ExtractionRowStatus status)
    {
        if (rowId == 0) return;

        var queued = await ExtractionQueue.ReadAsync(
            context => Persistence.ExtractionQueueStore.FindAsync(context, (long)rowId), null);

        if (queued?.ExtractionRowId is not > 0) return;

        await ReportExtractionRowOutcomeById(queued.ExtractionRowId.Value, status);
    }

    /// <summary>
    /// La stessa segnalazione, quando il chiamante ha già in mano la riga di estrazione del
    /// gestionale invece della riga di coda del WCS: è il caso di
    /// <see cref="DeleteLuFromExtractionQueue"/>, che la legge in cima al metodo con
    /// <c>GetExtractionLuRowId</c> perché la riga di coda, a quel punto, l'ha già cancellata.
    /// </summary>
    internal static Task ReportExtractionRowOutcomeById(decimal extractionRowId, ExtractionRowStatus status)
    {
        if (extractionRowId <= 0) return Task.CompletedTask;

        return Navigator.RequireWarehouse($"segnalazione dell'esito della riga di estrazione {extractionRowId}")
            .ReportExtractionOutcomesAsync(new ReportExtractionOutcomesRequest(
                0m, Navigator.WorkstationId, [new ExtractionRowOutcome(extractionRowId, status)]));
    }

    #endregion Methods

}
