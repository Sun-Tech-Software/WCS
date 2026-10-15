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

    #region Private Methods

    /// <summary>
    /// Rimette in coda di prelievo una missione ripresa all'avvio, o la toglie se non ha piu' un
    /// posto dove andare.
    /// </summary>
    /// <remarks>
    /// Interno e non privato come i gemelli della costruzione delle missioni: e' il punto in cui
    /// una lettura del gestionale decide se una missione viva o venga cancellata, e il collaudo
    /// entra da qui.
    /// </remarks>
    internal static async Task AddMissionToPickUpQueue(Items.PathMission? loadUnit)
    {
        Items.AutomationDevice? automationItem;

        switch (loadUnit)
        {
            case { MissionType: MissionTypes.ChangeStoreLocation } when loadUnit.RelocateItemId == Guid.Empty:
                {
                    if (loadUnit.LuId > 0)
                        WriteLog(
                            $"DEBUG INFO: Cancellazione MISSION_LU da AddMissionToPickUpQueue 1 UDC: {loadUnit.LuId} - {loadUnit.LuCode} Date: {DateTime.Now:dd/MM/yyyy HH:mm:ss}",
                            LogEntryType.Information);

                    await ActiveMissions.Instance.RemoveUnused(loadUnit);
                    break;
                }
            case { MissionType: MissionTypes.ChangeStoreLocation }:
                {
                    automationItem = PathDevices.Instance[loadUnit.RelocateItemId];

                    if (automationItem == null)
                    {
                        if (loadUnit.LuId > 0)
                            WriteLog(
                                $"DEBUG INFO: Cancellazione MISSION_LU da AddMissionToPickUpQueue 2 UDC: {loadUnit.LuId} - {loadUnit.LuCode} Date: {DateTime.Now:dd/MM/yyyy HH:mm:ss}",
                                LogEntryType.Information);

                        await ActiveMissions.Instance.RemoveUnused(loadUnit);
                    }
                    else
                    {
                        automationItem.EnqueueLuToPickUp(loadUnit);
                        PathMission.Add(loadUnit);

                        automationItem.Status = DeviceStatusTypes.Recovering;
                    }

                    break;
                }
            case { MissionType: MissionTypes.PickUpAndStoreAgain }:
                {
                    automationItem = PathDevices.Instance[loadUnit.RelocateItemId];

                    if (automationItem == null) return;

                    if (automationItem.BelongedGroup != null)
                    {
                        automationItem.BelongedGroup.EnqueueLuToPickUp(loadUnit);
                        PathMission.Add(loadUnit);
                        automationItem.BelongedGroup.Status = DeviceStatusTypes.Recovering;
                    }
                    else
                    {
                        automationItem.EnqueueLuToPickUp(loadUnit);
                        PathMission.Add(loadUnit);
                    }

                    automationItem.Status = DeviceStatusTypes.Recovering;
                    break;
                }
            case { BufferCellId: 0 }:
                {
                    if (loadUnit.LuId > 0)
                        WriteLog(
                            $"DEBUG INFO: Cancellazione MISSION_LU da AddMissionToPickUpQueue 3 UDC: {loadUnit.LuId} - {loadUnit.LuCode} Date: {DateTime.Now:dd/MM/yyyy HH:mm:ss}",
                            LogEntryType.Information);
                    await ActiveMissions.Instance.RemoveUnused(loadUnit);
                    break;
                }
            default:
                {
                    if (loadUnit == null) return;

                    var warehouse = Navigator.RequireWarehouse($"AddMissionToPickUpQueue sull'unita' di carico {loadUnit.LuId}");

                    // Compito 2: le aree del polmone vengono dal confine. Un guasto solleva e la
                    // missione resta dov'e' - la cancellazione qui sotto e' irreversibile, e la
                    // lista vuota che la comanda adesso puo' solo essere un dato. Solleva nel punto
                    // in cui sollevava gia' la ricerca che segue, sullo stesso confine e con lo
                    // stesso esito per il chiamante.
                    var areasFilter = await warehouse.GetPickingBufferAreasAsync(loadUnit.BufferCellId);

                    if (areasFilter.Count == 0)
                    {
                        if (loadUnit.LuId > 0)
                            WriteLog(
                                $"DEBUG INFO: Cancellazione MISSION_LU da AddMissionToPickUpQueue 4 UDC: {loadUnit.LuId} - {loadUnit.LuCode} Date: {DateTime.Now:dd/MM/yyyy HH:mm:ss}",
                                LogEntryType.Information);

                        await ActiveMissions.Instance.RemoveUnused(loadUnit);
                        return;
                    }

                    var luPosition = await warehouse.FindLoadUnitAsync(new FindLoadUnitRequest(
                        loadUnit.LuId, areasFilter, null, LoadUnitLockFilter.Unlocked, CellLockFilter.Unlocked));

                    if (luPosition != null)
                    {
                        var unloadingDoorCode = await warehouse.ChoosePickUpDoorAsync(luPosition.LoadUnitId, luPosition.CellId);

                        var itemSource = await GetItemId(unloadingDoorCode.DoorCode);

                        automationItem = PathDevices.Instance[itemSource];

                        if (automationItem == null) return;

                        automationItem = PathDevices.Instance[automationItem.RelocateItemId];

                        if (automationItem == null) return;

                        if (automationItem.BelongedGroup != null)
                        {
                            automationItem.BelongedGroup.EnqueueLuToPickUp(loadUnit);
                            PathMission.Add(loadUnit);
                            automationItem.BelongedGroup.Status = DeviceStatusTypes.Recovering;
                        }
                        else
                        {
                            automationItem.EnqueueLuToPickUp(loadUnit);
                            PathMission.Add(loadUnit);
                        }

                        automationItem.Status = DeviceStatusTypes.Recovering;
                    }
                    else
                    {
                        if (loadUnit.LuId > 0)
                            WriteLog(
                                $"DEBUG INFO: Cancellazione MISSION_LU da AddMissionToPickUpQueue 5 UDC: {loadUnit.LuId} - {loadUnit.LuCode} Date: {DateTime.Now:dd/MM/yyyy HH:mm:ss}",
                                LogEntryType.Information);
                        await ActiveMissions.Instance.RemoveUnused(loadUnit);
                    }

                    break;
                }
        }
    }

    private static async Task<decimal> GetIdAssegnazioneUdc(decimal luId)
    {
        if (luId == 0) return 0;

        return await ExtractionQueue.ReadAsync(
            async context => (await Persistence.ExtractionQueueStore.FindByLoadUnitAsync(context, luId))?.AssignmentId ?? 0, 0m);
    }

    private static async Task<decimal> GetIdEstrazioneUdc(decimal luId)
    {
        if (luId == 0) return 0;

        return await ExtractionQueue.ReadAsync(
            async context => (decimal)((await Persistence.ExtractionQueueStore.FindByLoadUnitAsync(context, luId))?.Id ?? 0), 0m);
    }

    /// <summary>
    /// Ricarica dal database del WCS le missioni di un tipo di operazione e di un filtro, nell'ordine
    /// di creazione, e le rimette sui dispositivi.
    /// </summary>
    /// <remarks>
    /// Prima leggeva <c>MISSION_LU</c> su SQL Server con un lettore e una callback <c>async void</c>
    /// per riga, che nessuno attendeva: la lista delle missioni da rimettere sui dispositivi poteva
    /// essere ancora incompleta quando il ciclo che la scorre partiva. Ora ogni missione viene
    /// preparata e attesa prima della successiva. La lettura del gruppo resta un'operazione unica,
    /// ma preparazione e ripristino sono isolati per riga: una missione malformata o un dispositivo
    /// guasto non fermano le altre. Tappa 2 del database del WCS e voce D185.
    /// </remarks>
    private static async Task LoadMissions(OperationTypes operationType, bool activeFilter)
    {
        var store = Navigator.Store;
        if (store == null)
        {
            WriteLog("LoadMissions: il database del WCS non e' aperto, nessuna missione ricaricata", LogEntryType.Warning);
            return;
        }

        List<Persistence.Entities.Mission> rows;
        try
        {
            await using (var context = store.CreateContext())
                rows = await Persistence.MissionStore.LoadAsync(context, operationType, activeFilter);
        }
        catch (Exception ex)
        {
            WriteLog(
                $"LoadMissions: errore nella lettura delle missioni {operationType} (ActiveFilter={activeFilter}): {ex.Message}\r\nStackTrace: {ex.StackTrace}",
                LogEntryType.Warning);
            return;
        }

        var missionsToLoadOnItems = new List<Items.PathMission?>();

        foreach (var row in rows)
        {
            try
            {
                await PrepareLoadedMission(Persistence.MissionStore.ToPathMission(row), missionsToLoadOnItems);
            }
            catch (Exception ex)
            {
                LogMissionRecoveryFailure("la preparazione", row.MissionId, row.LoadUnitId, ex);
            }
        }

        for (var index = missionsToLoadOnItems.Count - 1; index >= 0; index--)
        {
            var mission = missionsToLoadOnItems[index];
            try
            {
                await RestoreLoadedMissionOnItem(mission);
            }
            catch (Exception ex)
            {
                LogMissionRecoveryFailure("il ripristino sul dispositivo", mission?.MissionId ?? Guid.Empty,
                    mission?.LuId ?? 0, ex);
            }
        }
    }

    /// <summary>Rimette una missione gia' preparata sul dispositivo o nella coda corretti.</summary>
    private static async Task RestoreLoadedMissionOnItem(Items.PathMission? mission)
    {
        if (mission != null && mission.PositionItemId != Guid.Empty)
        {
            var automationItem = mission.PositionItem;

            // La rotta si ricostruisce da sola dalla posizione attuale (ActiveMissions.Add
            // con buildRoute), quindi un elemento intermedio sparito dal diagramma non e' un
            // problema. Lo sono questi due casi, in cui non c'e' niente da ricostruire, e
            // prima passavano in silenzio: la missione restava attiva e ferma, senza che
            // nulla lo dicesse. Ora lo dicono al registro del navigatore. Voce D80.
            if (automationItem == null)
            {
                mission.StatusDescription =
                    $"La posizione {mission.PositionItemId} non esiste piu' nel diagramma";
                Navigator.RecordNavigatorLog(null,
                    $"Missione {mission.MissionId} (UDC {mission.LuCode}): {mission.StatusDescription}. Ferma finche' un operatore non decide.",
                    LogEntryType.Warning);
                return;
            }

            if (mission.DestinationItemId is { } destinazione && destinazione != Guid.Empty &&
                mission.DestinationItem == null)
            {
                mission.StatusDescription =
                    $"La destinazione {destinazione} non esiste piu' nel diagramma";
                Navigator.RecordNavigatorLog(automationItem,
                    $"Missione {mission.MissionId} (UDC {mission.LuCode}): {mission.StatusDescription}. La rotta non si puo' costruire.",
                    LogEntryType.Warning);
            }

            switch (automationItem.DeviceType)
            {
                case DeviceTypes.Storage:
                    await AddMissionToPickUpQueue(mission);
                    break;

                case DeviceTypes.Automation:
                case DeviceTypes.Loading:
                case DeviceTypes.Buffer:
                case DeviceTypes.CircularDistributor:
                case DeviceTypes.GroupOfAutomationDevices:
                case DeviceTypes.LoadingAndBuffer:
                case DeviceTypes.None:
                default:
                    var oldStatus = automationItem.Status;
                    try
                    {
                        automationItem.Status = DeviceStatusTypes.Ready;
                        await automationItem.LoadLu(mission.MissionId);
                    }
                    finally
                    {
                        automationItem.Status = oldStatus;
                    }

                    break;
            }
        }
        else
        {
            await AddMissionToPickUpQueue(mission);
        }
    }

    private static void LogMissionRecoveryFailure(string phase, Guid missionId, decimal loadUnitId, Exception ex)
    {
        WriteLog(
            $"LoadMissions: errore durante {phase} della missione {missionId} (UDC {loadUnitId}): {ex.Message}\r\nStackTrace: {ex.StackTrace}",
            LogEntryType.Warning);
    }

    /// <summary>
    /// Un'estrazione ricaricata con il segno «segnalazioni partite» e senza il cambio cella registrato
    /// (specifica, risposta dell'8/10 al punto 3): il gestionale puo' aver saputo che la missione
    /// lascia l'assegnazione e che l'UDC esce dalla coda di estrazione, e l'host si e' fermato prima
    /// del commit del cambio, o la chiusura dopo un cambio rifiutato non e' riuscita
    /// (<c>MissionGateway.LeaveTheExtractionAsync</c>). Si chiude come dopo un cambio rifiutato:
    /// l'UDC resta nella sua cella senza ordine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// La missione non entra fra le attive, e la sua riga e la riga della coda di estrazione del WCS
    /// si cancellano: rimasta, la ripresa della coda di prelievo (<see cref="RecoverPickUpQueue"/>),
    /// non trovando una missione per l'UDC, ne farebbe un'estrazione nuova. Una riga di avviso lo
    /// dice.
    /// </para>
    /// <para>
    /// <b>Durante la ricarica al gestionale non si manda niente</b>: le segnalazioni non si
    /// rimandano, e la prenotazione di una cella che la missione avesse preso prima del riavvio si
    /// restituisce appena il motore va in Run, una volta (<see cref="ReservationsOfMissionsClosedOnReload"/>;
    /// specifica, risposta 10 dell'8/10 sera), anche se l'host si ferma prima del Run: l'elenco sta nel
    /// database del WCS (risposta 2 del 9/10). Se il gestionale allora non risponde, la raccoglie la
    /// sua scadenza.
    /// </para>
    /// </remarks>
    private static async Task CloseAnExtractionWhoseReportsWereSent(Items.PathMission mission)
    {
        WriteLog(
            $"LoadMissions: la missione {mission.MissionId} (UDC {mission.LuId}) e' un'estrazione ricaricata con le segnalazioni del cambio cella gia' partite e senza il cambio registrato: il gestionale puo' aver saputo che lascia l'assegnazione e che l'UDC esce dalla coda di estrazione. La missione si chiude, e l'UDC resta nella sua cella senza ordine; nessuna segnalazione e' rimandata al gestionale",
            LogEntryType.Warning);

        if (mission.ExtractionQueueId > 0)
        {
            try
            {
                await DeleteExtractionItemQueueById(mission.ExtractionQueueId);
            }
            catch (Exception ex)
            {
                WriteLog(
                    $"LoadMissions: la riga {mission.ExtractionQueueId} della coda di estrazione della missione chiusa {mission.MissionId} non e' stata cancellata, e va tolta a mano: {ex.Message}",
                    LogEntryType.Warning);
            }
        }

        // La riga si toglie dopo il lavoro della posizione, che la ricostruzione della missione ha fatto
        // partire in sottofondo (Items.PathMission.PositionWork) e che alla fine la riscrive: prima la
        // cancellazione poteva venire prima di lui, e la riga della missione chiusa tornava nel
        // database del WCS - alla prossima ricarica l'estrazione si chiudeva di nuovo. Lo si e' visto
        // come rosso intermittente di ChangeOfCellReportsOnReloadTest nella suite intera (revisione
        // finale del secondo giro).
        await mission.PositionWork;

        // Prima la prenotazione da restituire, poi la riga della missione: un riavvio fra le due
        // ricarica la missione e la richiude, mentre nell'ordine inverso la prenotazione si perdeva
        // (risposta 2 del proprietario del 9/10).
        await ReservationsOfMissionsClosedOnReload.AddAsync(mission.MissionId);
        PathMission.Remove(mission);
    }

    /// <summary>
    /// Prepara una missione appena ricaricata: risolve destinazione e sorgente dai codici porta,
    /// scarta (e cancella) quella che risulta gia' a destinazione, la registra fra le attive e la
    /// mette nella lista di quelle da rimettere sui dispositivi. Era la callback per riga di
    /// <see cref="LoadMissions"/>.
    /// </summary>
    /// <remarks>
    /// <c>internal</c> e non <c>private</c> perche' il suo unico chiamante di produzione e'
    /// <see cref="LoadMissions"/>, che gira dentro <c>RecoverFailure</c> e rimette in piedi tutto il
    /// motore: le prove del compito 21 sul punto di questa missione entrano di qui, non di la'.
    /// <para>
    /// Prima di tutto il segno delle segnalazioni del cambio cella (voce 39, secondo giro, compito 9):
    /// un'estrazione che lo porta si chiude qui e non si ricarica
    /// (<see cref="CloseAnExtractionWhoseReportsWereSent"/>).
    /// </para>
    /// </remarks>
    internal static async Task PrepareLoadedMission(Items.PathMission mission, List<Items.PathMission?> missionsToLoadOnItems)
    {
            if (mission.ChangeOfCellReportsSent)
            {
                if (mission.MissionType == MissionTypes.Extraction)
                {
                    await CloseAnExtractionWhoseReportsWereSent(mission);
                    return;
                }

                // Il cambio cella e' registrato (il tipo e' gia' quello nuovo): il segno non serve piu'.
                mission.ChangeOfCellReportsSent = false;
            }

            if (!string.IsNullOrEmpty(mission.StorageDoorCode))
            {
                var destinationItemId = await GetItemId(mission.StorageDoorCode);
                var destinationItem = PathDevices.Instance[destinationItemId];

                if (destinationItem is { DeviceType: DeviceTypes.Storage } &&
                    mission.IsReachableItem(destinationItem.RelocateItem))
                {
                    mission.DestinationItem = destinationItem;
                    mission.BufferCellId = 0;
                }
                else if (destinationItem != null && mission.IsReachableItem(destinationItem))
                {
                    mission.DestinationItemId = destinationItemId;
                    mission.BufferCellId = destinationItem.LinkedCellId;
                }
                else if (mission.DestinationItem != null && mission.IsReachableItem(mission.DestinationItem))
                {
                    mission.StorageDoorCode = await GetDoorCode(mission.DestinationItemId);
                    mission.BufferCellId = mission.DestinationItem.LinkedCellId;
                }
                else
                {
                    mission.DestinationItem = mission.PositionItem?.ReachableBuffers.FirstOrDefault();
                    if (mission.DestinationItem != null)
                    {
                        mission.StorageDoorCode = await GetDoorCode(mission.DestinationItemId);
                        mission.BufferCellId = mission.DestinationItem.LinkedCellId;
                    }
                    else
                    {
                        mission.StorageDoorCode = null;
                        mission.BufferCellId = 0;
                    }
                }
            }

            if (!string.IsNullOrEmpty(mission.PickUpDoorCode))
                mission.SourceItemId = await GetItemId(mission.PickUpDoorCode);

            mission.StepOrigin = StepTypes.Automatic;

            if (!string.IsNullOrEmpty(mission.PickUpDoorCode) && !string.IsNullOrEmpty(mission.StorageDoorCode) &&
                mission.PickUpDoorCode == mission.StorageDoorCode &&
                mission.MissionType != MissionTypes.PickUpAndStoreAgain &&
                mission.MissionType != MissionTypes.DefragmentCell)
            {
                var item = mission.PositionItem;

                if (item != null && item.DeviceType != DeviceTypes.Storage)
                {
                    if (item.DeviceType != DeviceTypes.Storage &&
                        item.DeviceType != DeviceTypes.GroupOfAutomationDevices)
                        await item.FireLuLoadStatusChanged();

                    if (mission.LuNoteAssignmentId != 0)
                    {
                        if (mission.LuId > 0)
                            WriteLog(
                                $"DEBUG INFO: Cancellazione ASSEGNAZIONE_NOTE da LoadMissions->IdAssegnazioneNota UDC: {mission.LuId} - {mission.LuCode} Date: {DateTime.Now:dd/MM/yyyy HH:mm:ss}",
                                LogEntryType.Information);

                        // Compito 21, terzo dei tre punti dello stesso fatto: la missione ricaricata
                        // all'avvio risulta gia' a destinazione (porta di prelievo e di deposito
                        // coincidono) e viene scartata qui sotto.
                        //
                        // Il GUID non viaggia, ed e' voluto: la missione ricaricata puo' portare
                        // tutt'e due gli identificativi, ma questo punto ha sempre guardato solo
                        // ID_ASSEGNAZIONE_UDC. Mandare anche il GUID cancellerebbe tutte le righe
                        // dell'assegnazione dell'unita' di carico invece di quella sola: e' un
                        // comportamento diverso, non una correzione di questo compito.
                        await RequireWarehouse("PrepareLoadedMission").ReportMissionLeftAssignmentUnservedAsync(
                            new MissionLeftAssignmentUnservedRequest(mission.MissionId, mission.LuId, Guid.Empty,
                                mission.LuNoteAssignmentId,
                                "la missione ricaricata ha la porta di prelievo uguale a quella di deposito"));
                    }

                    if (mission.ExtractionQueueId > 0)
                    {
                        if (mission.LuId > 0)
                        {
                            WriteLog(
                                $"DEBUG INFO: Cancellazione ASSEGNAZIONE_NOTE da LoadMissions->IdExtractionQueue UDC: {mission.LuId} - {mission.LuCode} Date: {DateTime.Now:dd/MM/yyyy HH:mm:ss}",
                                LogEntryType.Information);
                            WriteLog(
                                $"DEBUG INFO: Cancellazione ESTRAZIONI_UDC_AUTOMATICHE da LoadMissions->IdExtractionQueue UDC: {mission.LuId} - {mission.LuCode} Date: {DateTime.Now:dd/MM/yyyy HH:mm:ss}",
                                LogEntryType.Information);
                        }

                        // Compito 13: lo stesso fatto di poche righe sopra, con l'altro campo -
                        // qui l'assegnazione viene dalla riga di coda invece che dalla missione, e
                        // sono due righe diverse. Vale parola per parola la nota di
                        // ActiveMissions.Remove.
                        await ReportQueuedAssignmentLeftUnserved(mission.MissionId, mission.LuId,
                            mission.ExtractionQueueId,
                            "la missione ricaricata ha la porta di prelievo uguale a quella di deposito");
                        await DeleteExtractionItemQueueById(mission.ExtractionQueueId);
                    }
                }

                PathMission.Remove(mission);
                return;
            }

            // Compito 20: ex WmsBusinessArea.OperazioniUdc.GetQuotaUdc(mission.LuId). Non e'
            // un'operazione nuova del confine: la quota di deposito e' la profondita' di
            // LoadUnitLocation, cioe' la POSIZIONE della riga di POSIZIONI_UDC con ASSEGNAZIONE = 0
            // che GetQuotaUdc leggeva, e LocateLoadUnitAsync la prometteva gia'. «Non c'e' nessuna
            // posizione fisica» resta zero, come rispondeva la lettura di prima quando la riga
            // mancava; un gestionale che non risponde adesso solleva invece di lasciare zero in
            // silenzio (vedi il rapporto del compito 20).
            if (mission is { LuId: > 0, MissionType: MissionTypes.Extraction, StorageDepth: 0 })
                mission.StorageDepth =
                    (await Navigator.RequireWarehouse($"PrepareLoadedMission sull'unita' di carico {mission.LuId}")
                        .LocateLoadUnitAsync(mission.LuId))?.StoringDepth ?? 0;

            ActiveMissions.Instance.Add(mission);

            missionsToLoadOnItems.Add(mission);

            if (mission.DestinationItem != null && mission.DestinationItem.DeviceType != DeviceTypes.Storage &&
                mission.DestinationItem.DeviceType != DeviceTypes.GroupOfAutomationDevices)
                await mission.DestinationItem.FireLuLoadStatusChanged();
            if (mission.SourceItem != null && mission.SourceItem.DeviceType != DeviceTypes.Storage &&
                mission.SourceItem.DeviceType != DeviceTypes.GroupOfAutomationDevices)
                await mission.SourceItem.FireLuLoadStatusChanged();
    }

    private static async Task RecoverPickUpQueue(Items.AutomationDevice item)
    {
        if (item.QueuedLuToPickUpCount != 0 || item.LUtoPick != null)
            return;

        var dtLu = await GetLuToExtract(item);

        if (dtLu == null || dtLu.Rows.Count == 0)
            return;

        foreach (DataRow row in dtLu.Rows)
        {
            // La missione attiva con questo LuId (se c'e') porta gia' il codice come proprieta': non
            // serve chiederlo di nuovo al gestionale solo per il registro (compito 10). Un
            // FirstOrDefault al posto di Any perche' serve l'oggetto, non solo se esiste.
            var activeMission = row.GetValueOrThrow<decimal>("ID_UDC") > 0
                ? ActiveMissions.Instance.FirstOrDefault(x => x != null && x.LuId == row.GetValueOrThrow<decimal>("ID_UDC"))
                : null;

            if (activeMission != null)
            {
                WriteLog(
                    $"DEBUG INFO: Cancellazione ESTRAZIONI_UDC_AUTOMATICHE da RecoverPickUpQueue UDC: {row.GetValueOrThrow<decimal>("ID_UDC")} - {activeMission.LuCode} Date: {DateTime.Now:dd/MM/yyyy HH:mm:ss}",
                    LogEntryType.Information);

                await DeleteExtractionItemQueueById(row.GetValueOrThrow<decimal>("ID_ESTRAZIONE_UDC"));
                continue;
            }

            if (item != null &&
                item.QueuedLUtoPickUp.Any(x => x != null && x.LuId == row.GetValueOrThrow<decimal>("ID_UDC"))) continue;

            var navigatorCommand = new Command();

            MissionTypes missionType;

            if (!row.GetValueOrThrow<bool>("ESTRARRE"))
            {
                if (item == null) continue;

                // Nessuna cella collegata: zero e' un dato legittimo, non un difetto. Il gestionale
                // non configurato e' un'altra cosa - va detto, non aggirato con lo stesso zero (come
                // gia' fa PathMission.DescribeCellAsync, compito 7).
                decimal idMagazzino;
                if (item.LinkedCellId == 0)
                    idMagazzino = 0m;
                else if (Warehouse is { } warehouseRecovery)
                    idMagazzino = (await warehouseRecovery.DescribeCellAsync(item.LinkedCellId)).WarehouseId;
                else
                    throw new InvalidOperationException(
                        $"RecoverPickUpQueue su {item.ItemGuid}: Navigator.Warehouse non e' impostato.");

                // Compito 16: ex CelleMagazzino.CercaDestinazioneUdc. Il gemello gia' migrato e'
                // AutomationDeviceStorageManager.MoveObstacleLu, e questa richiesta e' la sua -
                // stesso magazzino calcolato allo stesso modo, stesse aree, stessa cella esclusa,
                // stessi criteri, stesso «assegna la posizione». Non e' una ricerca: con
                // AssignPosition la cella viene prenotata, ed e' l'esito a decidere se la missione
                // diventi un cambio di posizione o un'estrazione.
                //
                // Come li', si prenota e basta, senza il ciclo di vita del compito 17
                // (PathMission.ReserveOrKeepStorageAsync): quello poggia sulla missione, e qui la
                // missione non c'e' ancora - nasce sotto, con item.AddMissionToPickUp. Da cui
                // MissionId vuoto: il gestionale non scrive nessun legame per una richiesta senza
                // missione.
                //
                // Il ramo «se i servizi non ci sono, rispondi un risultato vuoto» sparisce: la
                // regola del confine e' che se il gestionale non risponde, la cosa si dice.
                var request = new ReserveStorageRequest(
                    Guid.Empty,
                    row.GetValueOrThrow<decimal>("ID_UDC"),
                    idMagazzino,
                    await Persistence.LocalQueries.GetAutomaticAreasByWarehouseId(idMagazzino),
                    [row.GetValueOrThrow<decimal>("ID_CELLA")],
                    [item.ItemGuid],
                    StorageAreaSelectionMode.All,
                    StorageCellSearchMode.ConnectedAutomaticOperator,
                    CellFullnessPreference.Fullest,
                    StorageSearchOperatorType.Automatic,
                    AssignPosition: true,
                    VerifyPosition: false,
                    WorkstationId,
                    UserId);

                var reservation = await RequireWarehouse($"RecoverPickUpQueue su {item.ItemGuid}")
                    .ReserveStorageAsync(request);

                missionType = MissionTypes.ChangeStoreLocation;

                navigatorCommand.Date = DateTime.Now;
                navigatorCommand.CommandType = Command.CommandTypes.MoveLuAutomatic;
                navigatorCommand.LuId = row.GetValueOrThrow<decimal>("ID_UDC");
                navigatorCommand.PickUpDoorCode = row.GetValueOrThrow<string?>("CODICE_PORTA");

                if (string.IsNullOrEmpty(reservation.DoorCode))
                {
                    missionType = MissionTypes.Extraction;

                    // Compito 16: ex CelleMagazzino.DecodificaCella, di cui si leggeva la sola porta
                    // A. Il ramo «se i servizi non ci sono, rispondi un risultato vuoto» sparisce -
                    // la regola del confine e' che se il gestionale non risponde la cosa si dice
                    // (RequireWarehouse). Resta invece la tolleranza per la cella polmone assente,
                    // che il metodo vecchio esprimeva con ?.CODICE_PORTA_A e DescribeCellAsync -
                    // valore non annullabile, assenza trattata come guasto - non esprime piu': zero
                    // e' un dato legittimo, come gia' per LinkedCellId qui sopra.
                    var bufferCellId = row.GetValueOrThrow<decimal>("ID_CELLA_POLMONE");
                    var bufferDoorCodeA = bufferCellId == 0
                        ? null
                        : (await RequireWarehouse($"DescribeCellAsync sulla cella polmone {bufferCellId}")
                            .DescribeCellAsync(bufferCellId)).DoorCodeA;

                    row["ID_ITEM_DESTINATION"] = await GetItemId(bufferDoorCodeA);

                    navigatorCommand.BufferCellId = bufferCellId;
                    navigatorCommand.StorageDoorCode = bufferDoorCodeA;
                }
                else
                {
                    navigatorCommand.StorageDoorCode = reservation.DoorCode;
                }

                navigatorCommand.Status = Command.StatusTypes.Done;

                navigatorCommand.WorkstationId = row.GetValueOrThrow<string?>("ID_POSTAZIONE");

                await item.AddMissionToPickUp(navigatorCommand, row, missionType);
            }
            else
            {
                // Compito 16: ex CelleMagazzino.DecodificaCella, di cui si leggeva la sola porta A -
                // stessa guardia sullo zero del ramo di cambio posizione qui sopra, e stesso ramo
                // «servizi assenti» che sparisce.
                var bufferCellId = row.GetValueOrThrow<decimal>("ID_CELLA_POLMONE");
                var bufferDoorCodeA = bufferCellId == 0
                    ? null
                    : (await RequireWarehouse($"DescribeCellAsync sulla cella polmone {bufferCellId}")
                        .DescribeCellAsync(bufferCellId)).DoorCodeA;

                missionType = MissionTypes.Extraction;

                navigatorCommand.Date = DateTime.Now;
                navigatorCommand.CommandType = Command.CommandTypes.MoveLuAutomatic;
                navigatorCommand.LuId = row.GetValueOrThrow<decimal>("ID_UDC");
                navigatorCommand.PickUpDoorCode = row.GetValueOrThrow<string?>("CODICE_PORTA");
                navigatorCommand.StorageDoorCode = bufferDoorCodeA;
                navigatorCommand.Status = Command.StatusTypes.Done;
                navigatorCommand.WorkstationId = row.GetValueOrThrow<string?>("ID_POSTAZIONE");

                navigatorCommand.BufferCellId = bufferCellId;

                item?.AddMissionToPickUp(navigatorCommand, row, missionType);
            }
        }
    }

    /// <summary>
    /// La seconda copia di "azzera lo stato delle operazioni su una cella" (compito 9, passo 1 del
    /// brief - la prima e' <see cref="Navigator.ResetCellOperationsStatus"/>): chiede sempre
    /// <see cref="IWarehouse.GetCellBusyStateAsync"/> (prima interrogava il deposito sempre e il
    /// prelievo solo se c'era gia' un'estrazione in coda localmente - due chiamate condizionali,
    /// migrate in una sola chiamata al gestionale che risponde entrambi gli stati insieme), ma usa il
    /// risultato di prelievo solo quando <see cref="NumExtractionsByCellId"/> (locale, invariato) e'
    /// positivo, esattamente come faceva l'originale: se non c'e' un'estrazione in coda localmente,
    /// il prelievo resta considerato libero indipendentemente da quello che risponde il gestionale.
    /// La decisione finale passa da <see cref="Navigator.ShouldClearCellOperations"/>, la stessa
    /// regola dell'altra copia.
    /// <para>
    /// <c>internal</c> invece di <c>private</c> (unico cambio di visibilita' del compito 9): il suo
    /// unico chiamante, <see cref="CheckOperation"/>, la avvolge in un <c>try/catch</c> che
    /// inghiottirebbe l'eccezione, e al tempo del compito 9 chiamava prima
    /// <c>DeleteQueueLuOfNoteDone</c> - cancellata dalla revisione del compito 6 - che su
    /// <c>Navigator.Database</c>, la connessione a SQL Server del gestionale (un vero server,
    /// assente nei test; dal compito 16 quel campo non esiste piu'), sarebbe morta prima
    /// di arrivare fin qui. E' lo stesso limite gia' incontrato per <c>VerifyStoreInfo</c>
    /// (compito 7). Resa raggiungibile dal test senza cambiarne il comportamento, sullo stesso
    /// schema di <c>AutomationDeviceStorageManager.BlocksStoring</c>.
    /// </para>
    /// </summary>
    internal static async Task ResetCellOperationsStatus(decimal cellId)
    {
        if (cellId == 0) return;

        var idAssegnazione = await NumExtractionsByCellId(cellId);

        var busyState = await RequireWarehouse($"ResetCellOperationsStatus sulla cella {cellId}").GetCellBusyStateAsync(cellId, 0);
        var effectiveBusyState = idAssegnazione > 0 ? busyState : busyState with { PickUp = false };

        if (ShouldClearCellOperations(effectiveBusyState)) await SetOperationLockingCell(cellId, StatoOperazioneCella.Nessuna);
    }

    #endregion Private Methods

}
