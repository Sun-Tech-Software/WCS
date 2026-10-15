using Wcs.Engine.Collections;
using Wcs.Common;
using Wcs.Engine.Items;
using Wcs.Engine.ORM;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Log.Interface;
using Wcs.Wms.Contracts;
using static Wcs.Engine.Navigator;
using PathMission = Wcs.Engine.Items.PathMission;

namespace Wcs.Engine;

public abstract partial class ExecutableCommands
{

    #region Internal Methods

    /// <summary>
    /// Costruisce una richiesta di movimentazione raggruppata per un'unità di carico.
    /// </summary>
    /// <param name="luId">ID dell'unità di carico da movimentare</param>
    /// <param name="bufferCellId">ID della cella buffer per il processo</param>
    /// <param name="sourceItemId">ID dell'item sorgente</param>
    /// <param name="destinationItemId">ID dell'item destinazione</param>
    /// <param name="cellId">ID della cella in cui si trova l'unità di carico: <see cref="PickUpDoorChoice"/>
    /// (compito 11) non la porta più, la sa gia' chi chiama.</param>
    /// <param name="unloadingDoorCode">Informazioni sulla porta di scarico</param>
    /// <param name="idWorkstation">ID della workstation associata</param>
    /// <param name="rowId">ID della riga di estrazione (se applicabile)</param>
    /// <param name="processingId">La lavorazione di estrazione servita da <paramref name="rowId"/>, arrivata con la riga
    /// pendente (compito 8): resta sulla riga di coda, che e' il solo posto da cui la chiusura del movimento la rilegge.
    /// Zero quando la riga non serve nessuna estrazione per unita' di carico.</param>
    /// <param name="noteProcessingId">La lavorazione della nota servita da <paramref name="idAssegnazioneUdc"/>, arrivata
    /// con l'assegnazione pendente (compito 9): resta sulla riga di coda, da cui la prende la missione. Zero quando la
    /// riga non serve nessuna nota. E' un campo diverso da <paramref name="processingId"/> - due tabelle diverse del
    /// gestionale, due chiusure diverse.</param>
    /// <remarks>
    /// Questo metodo è utilizzato quando la movimentazione deve essere gestita da un gruppo di dispositivi di automazione.
    /// </remarks>
    internal static async Task BuildGroupedMoveLu(decimal luId, decimal bufferCellId, Guid sourceItemId,
        Guid destinationItemId, decimal cellId, PickUpDoorChoice unloadingDoorCode, string? idWorkstation, decimal rowId,
        decimal idAssegnazioneUdc = 0, Guid? guidAssegnazioneUdc = null, decimal processingId = 0, decimal noteProcessingId = 0)
    {
        var automationItem = PathDevices.Instance[sourceItemId];

        if (automationItem == null) return;

        if (automationItem.DeviceType == Enums.DeviceTypes.Storage)
            automationItem = PathDevices.Instance[automationItem.RelocateItemId];

        if (automationItem == null || IsLuInExtraction(luId)) return;

        if (await DbInfo.IsLuPresentInExtranctionLu(luId))
        {
            await DbInfo.UpdateExtractionLu(luId, bufferCellId, rowId, processingId);
            return;
        }

        var row = new ExtractionQueueItem
        {
            BufferCellId = bufferCellId,
            DestinationItemId = destinationItemId,
            SourceItemId = sourceItemId,
            RelocatorItemId = automationItem.ItemGuid,
            WorkstationId = idWorkstation,
            LoadUnitId = luId,
            CellId = cellId,
            DoorCode = unloadingDoorCode.DoorCode,
            Extract = true,
            Processed = false
        };

        // La riga di estrazione e la sua lavorazione si scrivono insieme (compito 8): senza riga di
        // estrazione non c'e' lavorazione da chiudere, e un valore solo dei due non vuol dire niente.
        if (rowId > 0)
        {
            row.ExtractionRowId = rowId;
            row.ProcessingId = processingId;
        }

        // L'assegnazione di nota e la sua lavorazione si scrivono insieme (compito 9), per la
        // stessa ragione della coppia qui sopra: la riga che non serve nessuna nota non deve portare
        // una lavorazione che la missione dichiarerebbe evasa.
        if (idAssegnazioneUdc > 0)
        {
            row.AssignmentId = idAssegnazioneUdc;
            row.NoteProcessingId = noteProcessingId;
        }

        row.AssignmentGuid = guidAssegnazioneUdc;

        await ExtractionQueue.UpsertAsync(row);
    }

    /// <summary>
    /// Costruisce una richiesta di movimentazione singola per un'unità di carico.
    /// </summary>
    /// <param name="luId">ID dell'unità di carico da movimentare</param>
    /// <param name="bufferCellId">ID della cella buffer per il processo</param>
    /// <param name="sourceItemId">ID dell'item sorgente</param>
    /// <param name="itemDestination">ID dell'item destinazione</param>
    /// <param name="cellId">ID della cella in cui si trova l'unità di carico (compito 11)</param>
    /// <param name="unloadingDoorCode">Informazioni sulla porta di scarico</param>
    /// <param name="idWorkstation">ID della workstation associata</param>
    /// <param name="rowId">ID della riga di estrazione (se applicabile)</param>
    /// <param name="idAssegnazioneUdc">Assegnazione di nota che la riga serve, se viene da una nota di spedizione: la
    /// porta solo la riga della UDC voluta, non quelle di ricollocamento degli ostacoli, perche' il consumatore cancella
    /// l'assegnazione di una riga la cui UDC ha una missione diversa</param>
    /// <param name="guidAssegnazioneUdc">GUID della stessa assegnazione, che la missione eredita all'accodamento</param>
    /// <param name="processingId">La lavorazione di estrazione servita da <paramref name="rowId"/>, arrivata con la riga
    /// pendente (compito 8): la porta solo la riga della UDC voluta, come l'assegnazione di nota - le righe di
    /// ricollocamento degli ostacoli non servono nessuna riga di estrazione, e la chiusura non passa da loro.</param>
    /// <param name="noteProcessingId">La lavorazione della nota servita da <paramref name="idAssegnazioneUdc"/>, arrivata
    /// con l'assegnazione pendente (compito 9): la porta solo la riga della UDC voluta, insieme all'assegnazione, e per
    /// la stessa ragione - un ostacolo ricollocato non sta servendo nessuna nota.</param>
    /// <remarks>
    /// Le scritture in coda sono attese, non lanciate: la verifica «gia' in coda» dell'ostacolo successivo, o della
    /// prossima assegnazione dello stesso giro, legge il database e deve trovare la riga precedente.
    /// Gestisce anche il caso in cui esistano ostacoli, inserendo in coda anche le unità di carico che ostruiscono il percorso.
    /// </remarks>
    internal static async Task BuildSingleMoveLu(decimal luId, decimal bufferCellId, Guid sourceItemId, Guid itemDestination,
        decimal cellId, PickUpDoorChoice unloadingDoorCode, string? idWorkstation, decimal rowId, decimal idAssegnazioneUdc = 0,
        Guid? guidAssegnazioneUdc = null, decimal processingId = 0, decimal noteProcessingId = 0)
    {
        var automationItem = PathDevices.Instance[sourceItemId];

        if (automationItem == null) return;

        if (automationItem.DeviceType == Enums.DeviceTypes.Storage)
            automationItem = PathDevices.Instance[automationItem.RelocateItemId];

        if (automationItem == null || IsLuInExtraction(luId)) return;

        if (await DbInfo.IsLuPresentInExtranctionLu(luId))
        {
            await DbInfo.UpdateExtractionLu(luId, bufferCellId, rowId, processingId);
            return;
        }

        if (unloadingDoorCode.ObstacleCount == 0)
        {
            var row = new ExtractionQueueItem
            {
                BufferCellId = bufferCellId,
                DestinationItemId = itemDestination,
                SourceItemId = sourceItemId,
                RelocatorItemId = automationItem.ItemGuid,
                WorkstationId = idWorkstation,
                LoadUnitId = luId,
                CellId = cellId,
                DoorCode = unloadingDoorCode.DoorCode,
                Extract = true,
                Processed = false
            };

            // La riga di estrazione e la sua lavorazione si scrivono insieme (compito 8).
            if (rowId > 0)
            {
                row.ExtractionRowId = rowId;
                row.ProcessingId = processingId;
            }

            // L'assegnazione di nota e la sua lavorazione si scrivono insieme (compito 9).
            if (idAssegnazioneUdc > 0)
            {
                row.AssignmentId = idAssegnazioneUdc;
                row.NoteProcessingId = noteProcessingId;
            }

            row.AssignmentGuid = guidAssegnazioneUdc;

            await ExtractionQueue.UpsertAsync(row);
        }
        else
        {
            // Compito 11: l'elenco degli ostacoli arriva gia' con la scelta della porta - non serve
            // piu' una seconda domanda al gestionale (era WmsBusinessArea.OperazioniUdc.UdcOstacolo).
            foreach (var lu in unloadingDoorCode.ObstacleLoadUnitIds)
            {
                if (IsLuInExtraction(lu) || await DbInfo.IsLuPresentInExtranctionLu(lu))
                    continue;

                var relocateRow = new ExtractionQueueItem
                {
                    BufferCellId = bufferCellId,
                    SourceItemId = sourceItemId,
                    RelocatorItemId = automationItem.ItemGuid,
                    WorkstationId = idWorkstation,
                    LoadUnitId = lu,
                    CellId = cellId,
                    DoorCode = unloadingDoorCode.DoorCode,
                    Extract = false,
                    Processed = false
                };

                await ExtractionQueue.UpsertAsync(relocateRow);
            }

            var row = new ExtractionQueueItem
            {
                BufferCellId = bufferCellId,
                DestinationItemId = itemDestination,
                SourceItemId = sourceItemId,
                RelocatorItemId = automationItem.ItemGuid,
                WorkstationId = idWorkstation,
                LoadUnitId = luId,
                CellId = cellId,
                DoorCode = unloadingDoorCode.DoorCode,
                Extract = true,
                Processed = false
            };

            // La riga di estrazione e la sua lavorazione si scrivono insieme (compito 8).
            if (rowId > 0)
            {
                row.ExtractionRowId = rowId;
                row.ProcessingId = processingId;
            }

            // L'assegnazione di nota e la sua lavorazione si scrivono insieme (compito 9).
            if (idAssegnazioneUdc > 0)
            {
                row.AssignmentId = idAssegnazioneUdc;
                row.NoteProcessingId = noteProcessingId;
            }

            row.AssignmentGuid = guidAssegnazioneUdc;

            await ExtractionQueue.UpsertAsync(row);
        }
    }

    /// <summary>
    /// Cambia la posizione di stoccaggio di una o più unità di carico.
    /// </summary>
    /// <param name="navigatorCommand">Il comando di navigazione contenente le informazioni necessarie</param>
    /// <remarks>
    /// Questo metodo sposta logicamente le unità di carico da una posizione all'altra nel sistema di magazzino,
    /// aggiornando le informazioni nel database senza movimentare fisicamente le unità.
    /// </remarks>
    internal static async Task ChangeLUsStoreLocation(Command navigatorCommand)
    {
        if (string.IsNullOrEmpty(navigatorCommand.PickUpDoorCode) ||
            string.IsNullOrEmpty(navigatorCommand.StorageDoorCode) ||
            string.IsNullOrEmpty(WorkstationId))
            return;

        var errorResult = string.Empty;

        // Compito 9: ex WmsBusinessArea.CelleMagazzino.GetDatiCella(doorCode), ora
        // IWarehouse.DescribeCellsByDoorAsync - qui si usa solo .ID_CELLA (CellId), come gia' faceva
        // questo chiamante con .FirstOrDefault() sul risultato originale.
        var source = (await RequireWarehouse($"DescribeCellsByDoorAsync sulla porta {navigatorCommand.PickUpDoorCode}")
            .DescribeCellsByDoorAsync(navigatorCommand.PickUpDoorCode)).FirstOrDefault();
        var destination = (await RequireWarehouse($"DescribeCellsByDoorAsync sulla porta {navigatorCommand.StorageDoorCode}")
            .DescribeCellsByDoorAsync(navigatorCommand.StorageDoorCode)).FirstOrDefault();

        if (source == null || destination == null)
            errorResult = CommandMessages.strErrMoveLuItemError;
        else if (destination.CellId == 0)
            errorResult = string.Format(CommandMessages.strErrMoveLuDestNotExsits, navigatorCommand.PickUpDoorCode);
        else if (source.CellId == 0)
            errorResult = string.Format(CommandMessages.strErrMoveLuSourceNotExsits, navigatorCommand.PickUpDoorCode);

        if (!string.IsNullOrEmpty(errorResult))
        {
            await CommandToDoError(navigatorCommand, ExecuteMoveLuTypes.Manual, errorResult);
            return;
        }

        var sourceItemId = await DbInfo.GetItemId(navigatorCommand.PickUpDoorCode);
        var destinationItemId = await DbInfo.GetItemId(navigatorCommand.StorageDoorCode);
        var askingItem = PathDevices.Instance[destinationItemId];

        if (sourceItemId == Guid.Empty)
        {
            errorResult = string.Format(CommandMessages.strErrMoveLuSourceNoLink, navigatorCommand.PickUpDoorCode);
            await CommandToDoError(navigatorCommand, ExecuteMoveLuTypes.Manual, errorResult);
            RecordNavigatorLog(askingItem, errorResult, LogEntryType.Error);
            return;
        }

        if (destinationItemId == Guid.Empty)
        {
            errorResult = string.Format(CommandMessages.strErrMoveLuDestNoLink, navigatorCommand.StorageDoorCode);
            await CommandToDoError(navigatorCommand, ExecuteMoveLuTypes.Manual, errorResult);
            RecordNavigatorLog(askingItem, errorResult, LogEntryType.Error);
            return;
        }

        var sourceItem = PathDevices.Instance[sourceItemId];

        if (sourceItem == null || sourceItem.DeviceType != Enums.DeviceTypes.Storage)
        {
            errorResult = CommandMessages.strErrMoveLuNoStoreLocation;
            await CommandToDoError(navigatorCommand, ExecuteMoveLuTypes.Manual, errorResult);
            RecordNavigatorLog(askingItem, errorResult, LogEntryType.Warning);
            return;
        }

        if (sourceItem.RelocateItemId == Guid.Empty || PathDevices.Instance[sourceItem.RelocateItemId] == null)
        {
            errorResult = CommandMessages.strErrMoveLuStoreNoPickup;
            await CommandToDoError(navigatorCommand, ExecuteMoveLuTypes.Manual, errorResult);
            RecordNavigatorLog(askingItem, errorResult, LogEntryType.Error);
            return;
        }

        var automationItem = PathDevices.Instance[sourceItemId];
        if (automationItem?.DeviceType == Enums.DeviceTypes.Storage)
            automationItem = PathDevices.Instance[automationItem.RelocateItemId];

        if (source == null)
        {
            navigatorCommand.Status = Command.StatusTypes.Done;
            return;
        }

        // Compito 16: ex OperazioniUdc.UdcCella(cella, TipiPorta.A), di cui questo ciclo saltava le
        // righe assegnate. IWarehouse.GetCellContentAsync legge gia' le sole posizioni non
        // assegnate, ordinate per posizione, quindi il salto sparisce insieme alla colonna. Due cose
        // che il metodo vecchio aveva e qui non servono piu': la porta smette di essere un parametro
        // (serviva solo a ordinare al contrario per la porta B, e qui si passava la porta A, cioe'
        // l'ordine naturale), e il campo «unita' di carico virtuale» che veniva insieme non lo
        // leggeva nessuno di questi chiamanti. La lista non e' mai nulla: la guardia sul nulla
        // sparisce.
        var lUtoDownload = await RequireWarehouse($"GetCellContentAsync sulla cella {source.CellId}")
            .GetCellContentAsync(source.CellId);

        try
        {
            foreach (var luId in lUtoDownload)
            {
                if (IsLuInExtraction(luId))
                    continue;

                if (await DbInfo.IsLuPresentInExtranctionLu(luId))
                {
                    await DbInfo.UpdateExtractionLu(luId, destination?.CellId, 0);
                }
                else
                {
                    var row = new ExtractionQueueItem
                    {
                        BufferCellId = 0,
                        DestinationItemId = destinationItemId,
                        SourceItemId = sourceItemId,
                        RelocatorItemId = automationItem?.ItemGuid,
                        WorkstationId = WorkstationId,
                        LoadUnitId = luId,
                        CellId = source.CellId,
                        DoorCode = navigatorCommand.PickUpDoorCode,
                        DestinationCellId = destination?.CellId,
                        DestinationDoorCode = navigatorCommand.StorageDoorCode,
                        Extract = false,
                        Processed = false
                    };

                    await ExtractionQueue.UpsertAsync(row);
                }
            }
        }
        catch (QueuedWriteRejectedException ex)
        {
            // La riga di coda non e' stata scritta: lo scrittore si sta fermando, o la coda e'
            // piena. Prima la scrittura non era attesa (`_ =`) e il rifiuto non lo guardava
            // nessuno: il comando dell'operatore risultava eseguito con la coda che poteva essere
            // vuota. Il comando va in errore, e l'operatore lo ripete.
            await CommandToDoError(navigatorCommand, ExecuteMoveLuTypes.Manual,
                $"{CommandMessages.strErrMoveLuItemError} - {ex.Message}");
            return;
        }

        navigatorCommand.Status = Command.StatusTypes.Done;
    }

    /// <summary>
    /// Disabilita la funzionalità di deframmentazione del magazzino.
    /// </summary>
    /// <remarks>
    /// Se la deframmentazione è già disabilitata o non è configurata (None), il metodo non ha effetto.
    /// Un log informativo viene registrato quando lo stato cambia.
    /// </remarks>
    internal static void DisableDefragmentation()
    {
        if (Defragmentation == Enums.DefragmentationTypes.None ||
            Defragmentation == Enums.DefragmentationTypes.Disabled)
            return;

        Defragmentation = Enums.DefragmentationTypes.Disabled;

        WriteLog(
            $"Navigator Defragmentation Changed to: {Enum.GetName(typeof(Enums.DefragmentationTypes), Enums.DefragmentationTypes.Disabled)}",
            LogEntryType.Information);
    }

    /// <summary>
    /// Svuota logicamente una cella del magazzino basandosi sull'ID dell'unità di carico contenuta.
    /// </summary>
    /// <param name="navigatorCommand">Il comando di navigazione contenente le informazioni necessarie</param>
    /// <param name="moveLuType">La modalità di esecuzione (automatica o manuale)</param>
    /// <remarks>
    /// Questo metodo cerca l'unità di carico specificata e organizza lo svuotamento logico della cella che la contiene.
    /// </remarks>
    internal static async Task EmptyCellLogically(Command navigatorCommand, ExecuteMoveLuTypes moveLuType)
    {
        if (PathDevices.Instance.Count == 0)
        {
            navigatorCommand.Status = Command.StatusTypes.Done;
            return;
        }

        var idItem = await DbInfo.GetItemId(navigatorCommand.BufferCellId);
        var item = PathDevices.Instance[idItem];

        if (item == null)
        {
            // C38: dispositivo bersaglio non risolto (BufferCellId inesistente o rimosso):
            // segnala l'errore invece di chiudere il comando come se fosse andato a buon fine.
            navigatorCommand.ErrorDescription = $"Target device not found for BufferCellId '{navigatorCommand.BufferCellId}'";
            RecordNavigatorLog(null, navigatorCommand.ErrorDescription, LogEntryType.Warning);
            navigatorCommand.Status = Command.StatusTypes.Error;
            return;
        }

        var warehouse = RequireWarehouse($"EmptyCellLogically sull'unita' di carico {navigatorCommand.LuId}");

        // Compito 2: le aree del polmone vengono dal confine. Un guasto solleva, e il comando si
        // chiude in errore nel catch per comando di CommandToDo.Execute con il messaggio che
        // l'operatore legge: non resta ne' Running ne' Done. La lista vuota qui sotto e' adesso solo
        // un dato - «questo polmone non serve nessuna area», da cui Done e' la risposta giusta.
        var areasFilter = await warehouse.GetPickingBufferAreasAsync(navigatorCommand.BufferCellId);

        if (areasFilter.Count == 0)
        {
            navigatorCommand.Status = Command.StatusTypes.Done;
            return;
        }

        var luPosition = await warehouse.FindLoadUnitAsync(new FindLoadUnitRequest(
            navigatorCommand.LuId, areasFilter, null, LoadUnitLockFilter.Unlocked, CellLockFilter.FreeForUnloading));

        if (luPosition != null)
        {
            var unloadingDoorCode = await warehouse.ChoosePickUpDoorAsync(luPosition.LoadUnitId, luPosition.CellId);

            var errorDescription = await EmptyCellLogically(unloadingDoorCode.DoorCode, navigatorCommand.StorageDoorCode,
                navigatorCommand.WorkstationId);

            if (!string.IsNullOrEmpty(errorDescription))
                await CommandToDoError(navigatorCommand, moveLuType, errorDescription);
            else
                navigatorCommand.Status = Command.StatusTypes.Done;
        }
        else
        {
            var luCode = (await warehouse.DescribeLoadUnitAsync(navigatorCommand.LuId))?.Code;
            var errorDescription = string.Format(CommandMessages.strErrMoveLuNotFound,
                luCode, navigatorCommand.StorageDoorCode);
            RecordNavigatorLog(item, errorDescription, LogEntryType.Warning);
            await CommandToDoError(navigatorCommand, moveLuType, errorDescription);
        }
    }

    /// <summary>
    /// Svuota fisicamente una cella del magazzino.
    /// </summary>
    /// <param name="navigatorCommand">Il comando di navigazione contenente le informazioni necessarie</param>
    /// <param name="moveLuType">La modalità di esecuzione (automatica o manuale)</param>
    /// <remarks>
    /// A differenza dello svuotamento logico, questo metodo avvia una movimentazione fisica delle unità di carico.
    /// </remarks>
    internal static async Task EmptyCellPhisically(Command navigatorCommand, ExecuteMoveLuTypes moveLuType)
    {
        var errorDescription = await EmptyCellPisically(navigatorCommand.PickUpDoorCode, navigatorCommand.StorageDoorCode,
            navigatorCommand.WorkstationId, navigatorCommand.LuTypeId);

        if (!string.IsNullOrEmpty(errorDescription))
            await CommandToDoError(navigatorCommand, moveLuType, errorDescription);
        else
            navigatorCommand.Status = Command.StatusTypes.Done;
    }

    /// <summary>
    /// Abilita la funzionalità di deframmentazione del magazzino.
    /// </summary>
    /// <remarks>
    /// Se la deframmentazione è già abilitata o non è configurata (None), il metodo non ha effetto.
    /// Un log informativo viene registrato quando lo stato cambia.
    /// </remarks>
    internal static void EnableDefragmentation()
    {
        if (Defragmentation == Enums.DefragmentationTypes.None || Defragmentation == Enums.DefragmentationTypes.Enabled)
            return;

        Defragmentation = Enums.DefragmentationTypes.Enabled;

        WriteLog(
            $"Navigator Defragmentation Changed to: {Enum.GetName(typeof(Enums.DefragmentationTypes), Enums.DefragmentationTypes.Enabled)}",
            LogEntryType.Information);
    }

    /// <summary>
    /// Esegue gli step di processo per tutti i dispositivi del sistema.
    /// </summary>
    /// <remarks>
    /// Questo è il metodo principale che fa avanzare il sistema di automazione, processando ogni dispositivo
    /// in base al suo stato attuale e alle missioni assegnate.
    /// </remarks>
    internal static void ExecuteSteps()
    {
        if (PathDevices.Instance.Count == 0 || NavStatus != Enums.NavigatorStatusTypes.Run)
            return;

        ExtractionTimers = false;

        var devicesList = PathDevices.Instance.List;

        // Ottimizzazione: un solo ciclo per processare tutti gli elementi
        foreach (var item in devicesList)
        {
            // L'operatore rimette in marcia tutti i dispositivi: anche la fila degli errori di deposito
            // ricomincia, come con «esegui step» sul dispositivo e con «tutti pronti» (risposta 1 del
            // proprietario dell'8/10). Senza, il dispositivo andato in errore al quarto rifiuto uguale
            // tornava pronto con il conto a quattro, e al rifiuto successivo ricadeva in errore senza
            // cercare: lo stesso dispositivo si comportava in modo diverso secondo il comando usato.
            item.ForgetRefusedDeposits();

            var needsReset = item.BelongedItems.Count == 0 &&
                             item.DeviceType != Enums.DeviceTypes.CircularDistributor &&
                             item.Status != Enums.DeviceStatusTypes.OffLine &&
                             item.Status != Enums.DeviceStatusTypes.Ready;

            var needsExecution = item.MissionSteps.Count > 0 ||
                                 item.IdCellToDefragment > 0 ||
                                 item.DeviceType == Enums.DeviceTypes.GroupOfAutomationDevices ||
                                 item.DeviceType == Enums.DeviceTypes.CircularDistributor;

            // Resetta lo stato se necessario
            if (needsReset)
            {
                // Anche lo step in corso, come fa gia' ClearError. Riportare a Ready un dispositivo
                // che porta ancora addosso RunningStepId lo lascerebbe «impegnato»: la riesecuzione
                // qui sotto verrebbe rifiutata dalla guardia di ExecuteStep e il dispositivo non
                // ripartirebbe piu'. Azzerarlo libera anche il posto preso sulla destinazione.
                item.RunningStepId = Guid.Empty;
                item.Status = Enums.DeviceStatusTypes.Ready;
                item.LUtoReceive = null;
                item.ClearDevicesReadyToTransferLu();
            }

            // Esegue i passi se necessario
            if (needsExecution)
            {
                item.IdCellToDefragment = 0;

                switch (item.DeviceType)
                {
                    case Enums.DeviceTypes.GroupOfAutomationDevices:
                    case Enums.DeviceTypes.CircularDistributor:
                        _ = item.ExecuteStep();
                        break;
                    default:
                        if (item.LUsLoaded.Count > 0 && item.DeviceType != Enums.DeviceTypes.Storage &&
                            !item.IsLinkedToStorage)
                            _ = item.ExecuteStep();
                        break;
                }
            }
        }

        ExtractionTimers = true;
    }

    /// <summary>
    /// Esegue uno step specifico per un dispositivo identificato nel comando.
    /// </summary>
    /// <param name="navigatorCommand">Il comando di navigazione contenente le informazioni necessarie</param>
    /// <remarks>
    /// Permette di forzare l'esecuzione di uno step su un dispositivo specifico.
    /// </remarks>
    internal static async Task ItemExecuteStep(Command navigatorCommand)
    {
        if (PathDevices.Instance.Count == 0)
        {
            navigatorCommand.Status = Command.StatusTypes.Done;
            return;
        }

        var item = PathDevices.Instance[navigatorCommand.ItemCommandId];

        if (item == null)
        {
            // C38: dispositivo bersaglio non risolto (ItemCommandId inesistente/rimosso):
            // segnala l'errore invece di chiudere il comando come se fosse andato a buon fine.
            navigatorCommand.ErrorDescription = $"Target device not found for ItemCommandId '{navigatorCommand.ItemCommandId}'";
            RecordNavigatorLog(null, navigatorCommand.ErrorDescription, LogEntryType.Warning);
            navigatorCommand.Status = Command.StatusTypes.Error;
            return;
        }

        // L'operatore rimette in marcia il dispositivo: la fila degli errori di deposito ricomincia
        // (risposta 1 del proprietario dell'8/10). Dopo il quarto errore uguale il dispositivo e' in
        // errore e aspetta proprio questo; anche se adesso il comando e' rifiutato, chi lo ha dato ha
        // guardato il dispositivo. Ricomincia per tutta la famiglia che il comando rimette in marcia:
        // «esegui step» sul gruppo riporta a Ready i membri in errore (GroupDevice ->
        // ResetStatusBelongedItems), e su un membro riporta a Ready il gruppo e gli altri membri
        // (Grouped). Prima ricominciava solo il dispositivo del comando: il membro fermo al quarto 85
        // tornava pronto dal gruppo con il conto a quattro, e il quinto 85 lo rimandava in errore senza
        // cercare. Qui e non in ResetStatusBelongedItems, dove arrivano anche le ripartenze automatiche.
        foreach (var restarted in TheDevicesTheRestartReaches(item))
            restarted.ForgetRefusedDeposits();

        // Il rifiuto deve arrivare all'operatore. Se preme «esegui step» su un elemento
        // impegnato, il comando si chiude in errore con il motivo: prima l'esito era invisibile,
        // e con la riesecuzione differita sarebbe stato peggio -- il movimento sarebbe partito
        // molto dopo, quando nessuno se lo aspettava piu'.
        if (await item.ExecuteStep()) return;

        navigatorCommand.ErrorDescription =
            $"Device {item.ASI}.{item.SUBITEM}.{item.PARTITION} busy: execute step refused";
        RecordNavigatorLog(item, navigatorCommand.ErrorDescription, LogEntryType.Warning);
        navigatorCommand.Status = Command.StatusTypes.Error;
    }

    /// <summary>
    /// I dispositivi che «esegui step» su <paramref name="item"/> rimette in marcia: lui, e se e' un
    /// gruppo o il membro di un gruppo, il gruppo e tutti i suoi membri.
    /// </summary>
    private static IEnumerable<Items.AutomationDevice> TheDevicesTheRestartReaches(Items.AutomationDevice item)
    {
        var group = item.DeviceType == Enums.DeviceTypes.GroupOfAutomationDevices ? item : item.BelongedGroup;
        if (group == null) return [item];

        return new[] { item, group }.Concat(group.BelongedItems.OfType<Items.AutomationDevice>()).Distinct();
    }

    /// <summary>
    /// Trova una posizione di stoccaggio per l'unità di carico attualmente caricata sul dispositivo specificato.
    /// </summary>
    /// <param name="navigatorCommand">Il comando di navigazione contenente le informazioni necessarie</param>
    /// <remarks>
    /// Questo metodo interagisce con il gestore dello stoccaggio per determinare la posizione ottimale
    /// in cui stoccare l'unità di carico, in base alle regole configurate.
    /// </remarks>
    internal static async Task ItemFindStoringLocation(Command navigatorCommand)
    {
        if (PathDevices.Instance.Count == 0)
        {
            navigatorCommand.Status = Command.StatusTypes.Done;
            return;
        }

        var item = PathDevices.Instance[navigatorCommand.ItemCommandId];

        if (item == null || item.LoadedLu == null)
        {
            navigatorCommand.Status = Command.StatusTypes.Done;
            return;
        }

        // «Impegnato» in aggiunta allo stato: un dispositivo puo' risultare Ready e avere
        // comunque un comando in volo verso il campo, o un'esecuzione gia' dentro il semaforo.
        // Un elemento che sta eseguendo un comando non ne accetta altri.
        if (item.IsBusyWithCommand ||
            (item.Status != Enums.DeviceStatusTypes.Ready && item.Status != Enums.DeviceStatusTypes.Warning))
        {
            navigatorCommand.ErrorDescription = CommandMessages.strErrMoveLuSearchLuItemNotReady;
            RecordNavigatorLog(item, navigatorCommand.ErrorDescription, LogEntryType.Warning);
            navigatorCommand.Status = Command.StatusTypes.Error;
        }
        else if (item.LUsLoaded.Count == 0)
        {
            navigatorCommand.ErrorDescription = CommandMessages.strErrMoveLuSearchLuItemNoLu;
            RecordNavigatorLog(item, navigatorCommand.ErrorDescription, LogEntryType.Warning);
            navigatorCommand.Status = Command.StatusTypes.Error;
        }
        else if (item.DeviceType != Enums.DeviceTypes.GroupOfAutomationDevices && item.LoadedLu.LuId == 0)
        {
            navigatorCommand.ErrorDescription = CommandMessages.strErrMoveLuSearchLuItemLuNoCode;
            RecordNavigatorLog(item, navigatorCommand.ErrorDescription, LogEntryType.Warning);
            navigatorCommand.Status = Command.StatusTypes.Error;
        }
        else
        {
            // Nessun deposito rifiutato dal PLC: una missione mandata dall'operatore in un magazzino
            // tiene la sua cella (la risposta 1 del proprietario dell'8/10 vale per la cella rifiutata).
            if (item.StorageManager != null && await item.StorageManager.FindStoringLocation())
                switch (item.DeviceType)
                {
                    case Enums.DeviceTypes.GroupOfAutomationDevices:
                        {
                            await Task.Delay(500);

                            item.Status = Enums.DeviceStatusTypes.Ready;
                            item.GroupExecuteStepRunning = false;
                            break;
                        }

                    default:
                        {
                            await Task.Delay(50);

                            if (item.AutoExecuteStep)
                                _ = item.ExecuteStep();
                            break;
                        }
                }

            navigatorCommand.Status = Command.StatusTypes.Done;
        }
    }

    /// <summary>
    /// Rifiuta l'unità di carico attualmente presente sul dispositivo specificato.
    /// </summary>
    /// <param name="navigatorCommand">Il comando di navigazione contenente le informazioni necessarie</param>
    /// <remarks>
    /// Questo metodo è utilizzato quando un'unità di carico deve essere respinta, ad esempio
    /// per non conformità o per altri motivi operativi.
    /// </remarks>
    internal static async Task ItemRejectLu(Command navigatorCommand)
    {
        if (PathDevices.Instance.Count == 0)
        {
            navigatorCommand.Status = Command.StatusTypes.Done;
            return;
        }

        var item = PathDevices.Instance[navigatorCommand.ItemCommandId];

        if (item == null)
        {
            // C38: dispositivo bersaglio non risolto (ItemCommandId inesistente/rimosso):
            // segnala l'errore invece di chiudere il comando come se fosse andato a buon fine.
            navigatorCommand.ErrorDescription = $"Target device not found for ItemCommandId '{navigatorCommand.ItemCommandId}'";
            RecordNavigatorLog(null, navigatorCommand.ErrorDescription, LogEntryType.Warning);
            navigatorCommand.Status = Command.StatusTypes.Error;
            return;
        }

        // «Impegnato» in aggiunta allo stato: un dispositivo puo' risultare Ready e avere
        // comunque un comando in volo verso il campo, o un'esecuzione gia' dentro il semaforo.
        // Un elemento che sta eseguendo un comando non ne accetta altri.
        if (item.IsBusyWithCommand ||
            (item.Status != Enums.DeviceStatusTypes.Ready && item.Status != Enums.DeviceStatusTypes.Warning))
        {
            navigatorCommand.ErrorDescription = CommandMessages.strErrMoveLuRejectLuItemNotReady;
            RecordNavigatorLog(item, navigatorCommand.ErrorDescription, LogEntryType.Warning);
            navigatorCommand.Status = Command.StatusTypes.Error;
        }
        else if (item.LUsLoaded.Count == 0)
        {
            navigatorCommand.ErrorDescription = CommandMessages.strErrMoveLuRejectLuItemNoLu;
            RecordNavigatorLog(item, navigatorCommand.ErrorDescription, LogEntryType.Warning);
            navigatorCommand.Status = Command.StatusTypes.Error;
        }
        else if (item.LUsLoaded.Any(x => x?.GatewayItem != null))
        {
            navigatorCommand.ErrorDescription = CommandMessages.strErrMoveLuRejectLuItemTrasfering;
            RecordNavigatorLog(item, navigatorCommand.ErrorDescription, LogEntryType.Warning);
            navigatorCommand.Status = Command.StatusTypes.Error;
        }
        else
        {
            await item.RejectLu();
            navigatorCommand.Status = Command.StatusTypes.Done;
        }
    }

    /// <summary>
    /// Segna come completato uno step di processo per un dispositivo.
    /// </summary>
    /// <param name="navigatorCommand">Il comando di navigazione contenente le informazioni necessarie</param>
    /// <remarks>
    /// Questo metodo è utilizzato per notificare al sistema che uno step di processo è stato completato,
    /// consentendo al sistema di avanzare al passo successivo.
    /// </remarks>
    internal static async Task ItemStepDone(Command navigatorCommand)
    {
        if (PathDevices.Instance.Count == 0)
        {
            navigatorCommand.Status = Command.StatusTypes.Done;
            return;
        }

        var item = PathDevices.Instance[navigatorCommand.ItemCommandId];

        if (item == null)
        {
            // C38: dispositivo bersaglio non risolto (ItemCommandId inesistente/rimosso):
            // segnala l'errore invece di chiudere il comando come se fosse andato a buon fine.
            navigatorCommand.ErrorDescription = $"Target device not found for ItemCommandId '{navigatorCommand.ItemCommandId}'";
            RecordNavigatorLog(null, navigatorCommand.ErrorDescription, LogEntryType.Warning);
            navigatorCommand.Status = Command.StatusTypes.Error;
            return;
        }

        switch (item.DeviceType)
        {
            case Enums.DeviceTypes.GroupOfAutomationDevices:
                {
                    if (item.GroupedMoveLuTo)
                        await item.StepDoneGroup(item.RunningStepId, Enums.StepTypes.Manual);
                    else
                        // La guardia esplicita sostituisce "await childItem?.StepDone(...)", che su un
                        // elemento nullo avrebbe sollevato NullReferenceException (l'await su un
                        // Task nullo) invece di saltarlo. Voce D49.
                        foreach (var childItem in item.BelongedItems.Where(x => x != null && x.RunningStepId != Guid.Empty))
                            if (childItem != null)
                                await childItem.StepDone(childItem.RunningStepId, Enums.StepTypes.Manual);

                    break;
                }
            case Enums.DeviceTypes.None:
            case Enums.DeviceTypes.Storage:
                break;

            default:
                {
                    if (item.BelongedGroup == null)
                        await item.StepDone(item.RunningStepId, Enums.StepTypes.Manual);
                    else if (item.BelongedGroup.GroupedMoveLuTo)
                        await item.BelongedGroup.StepDoneGroup(item.BelongedGroup.RunningStepId, Enums.StepTypes.Manual);
                    else
                        // Stessa guardia del ramo di gruppo qui sopra. Voce D49.
                        foreach (var childItem in item.BelongedGroup.BelongedItems)
                            if (childItem != null)
                                await childItem.StepDone(childItem.RunningStepId, Enums.StepTypes.Manual);

                    break;
                }
        }
    }

    /// <summary>
    /// Scarica l'unità di carico attualmente presente sul dispositivo specificato.
    /// </summary>
    /// <param name="navigatorCommand">Il comando di navigazione contenente le informazioni necessarie</param>
    /// <remarks>
    /// Questo metodo esegue lo scarico dell'unità di carico, rimuovendola dal sistema di automazione.
    /// Non è possibile se la funzionalità di scarico è disabilitata o se il dispositivo non è in stato Ready o Warning.
    /// </remarks>
    internal static async Task ItemUnloadLu(Command navigatorCommand)
    {
        if (PathDevices.Instance.Count == 0)
        {
            navigatorCommand.Status = Command.StatusTypes.Done;
            return;
        }

        var item = PathDevices.Instance[navigatorCommand.ItemCommandId];

        if (item == null)
        {
            // C38: dispositivo bersaglio non risolto (ItemCommandId inesistente/rimosso):
            // segnala l'errore invece di chiudere il comando come se fosse andato a buon fine.
            navigatorCommand.ErrorDescription = $"Target device not found for ItemCommandId '{navigatorCommand.ItemCommandId}'";
            RecordNavigatorLog(null, navigatorCommand.ErrorDescription, LogEntryType.Warning);
            navigatorCommand.Status = Command.StatusTypes.Error;
            return;
        }

        if (DisableUnloadLu)
        {
            navigatorCommand.ErrorDescription = CommandMessages.strErrMoveLuUnloadDisabled;
            RecordNavigatorLog(item, navigatorCommand.ErrorDescription, LogEntryType.Warning);
            navigatorCommand.Status = Command.StatusTypes.Error;
        }
        // «Impegnato» in aggiunta allo stato: si veda la nota negli altri due comandi.
        else if (item.IsBusyWithCommand ||
                 (item.Status != Enums.DeviceStatusTypes.Ready && item.Status != Enums.DeviceStatusTypes.Warning))
        {
            navigatorCommand.ErrorDescription = CommandMessages.strErrMoveLuUnloadLuItemNotReady;
            RecordNavigatorLog(item, navigatorCommand.ErrorDescription, LogEntryType.Warning);
            navigatorCommand.Status = Command.StatusTypes.Error;
        }
        else if (item.LUsLoaded.Count == 0)
        {
            navigatorCommand.ErrorDescription = CommandMessages.strErrMoveLuUnloadLuItemNoLu;
            RecordNavigatorLog(item, navigatorCommand.ErrorDescription, LogEntryType.Warning);
            navigatorCommand.Status = Command.StatusTypes.Error;
        }
        else
        {
            await item.LuUnloadedFromAsi(Enums.StepTypes.Manual);
            navigatorCommand.Status = Command.StatusTypes.Done;
        }
    }

    /// <summary>
    /// Carica un'unità di carico sul dispositivo specificato.
    /// </summary>
    /// <param name="navigatorCommand">Il comando di navigazione contenente le informazioni necessarie</param>
    /// <remarks>
    /// Questo metodo crea una nuova missione di carico per l'unità di carico specificata e la assegna al dispositivo.
    /// </remarks>
    internal static async Task LoadLu(Command navigatorCommand)
    {
        if (PathDevices.Instance.Count == 0 || NavStatus != Enums.NavigatorStatusTypes.Run)
        {
            // R7: un comando che arriva qui NON ha fatto nulla - l'impianto non e' caricato oppure
            // il navigatore non e' in Run - e chiuderlo come Done diceva all'operatore che era
            // riuscito. Stessa forma applicata dalla voce C38 agli altri punti.
            navigatorCommand.ErrorDescription = PathDevices.Instance.Count == 0
                ? "No devices loaded: the plant diagram is not in memory"
                : $"Navigator is not running (status {NavStatus})";
            RecordNavigatorLog(null, navigatorCommand.ErrorDescription, LogEntryType.Warning);
            navigatorCommand.Status = Command.StatusTypes.Error;
            return;
        }

        var item = PathDevices.Instance[navigatorCommand.ItemCommandId];

        if (item is not { LoadedLu: null })
        {
            navigatorCommand.Status = Command.StatusTypes.Done;
            return;
        }

        // Compito 10: una sola domanda al gestionale per codice, tipo, modalita' di movimentazione e
        // tara - punto di costruzione non fra i quattro ufficiali del brief, trovato cercando per
        // nome invece di fidarsi dell'elenco (LoadLu costruisce una missione con LuId reale come
        // AddMissionToPickUp/AddMissionToGroupPickUp).
        var loadUnitDescription = navigatorCommand.LuId > 0
            ? await RequireWarehouse($"LoadLu sull'unita' di carico {navigatorCommand.LuId}").DescribeLoadUnitAsync(navigatorCommand.LuId)
            : null;

        PathMission mission = new()
        {
            LuId = navigatorCommand.LuId,
            SourceItemId = navigatorCommand.ItemCommandId,
            DestinationItemId = Guid.Empty,
            StorageDoorCode = string.Empty,
            IdCommandToDo = navigatorCommand.IdCommandToDo,
            LuCode = loadUnitDescription?.Code ?? string.Empty,
            IdLuType = loadUnitDescription?.LoadUnitTypeId ?? 0,
            HandlingMode = (int)Math.Round(loadUnitDescription?.HandlingMode ?? 0),
            Tare = loadUnitDescription?.Tare ?? 0,
            Date = DateTime.Now,
            OperationType = Enums.OperationTypes.Inbound,
            StepOrigin = Enums.StepTypes.Manual
        };

        if (item.LuManager != null && !item.LuManager.CanAcceptLu(mission)) return;

        ActiveMissions.Instance.Add(mission);
        await item.LoadLu(mission.MissionId);
    }

    /// <summary>
    /// Esegue la rotta di prova dell'Editor: costruisce una missione senza UDC (nessun <c>LuId</c>,
    /// nessun codice) dal dispositivo sorgente alla destinazione indicata, e la carica come farebbe
    /// un ingresso manuale. E' l'unico modo per l'operatore di misurare i tempi di una rotta senza
    /// passare per il comando 3007, che rifiuta <c>LuId == 0</c> perche' non trova il codice UDC.
    /// </summary>
    /// <param name="navigatorCommand">Il comando di navigazione con dispositivo sorgente, destinazione e tipo UDC</param>
    internal static async Task TestRoute(Command navigatorCommand)
    {
        if (NavStatus != Enums.NavigatorStatusTypes.Run)
        {
            navigatorCommand.ErrorDescription = $"Navigator is not running (status {NavStatus})";
            RecordNavigatorLog(null, navigatorCommand.ErrorDescription, LogEntryType.Warning);
            navigatorCommand.Status = Command.StatusTypes.Error;
            return;
        }

        var item = PathDevices.Instance[navigatorCommand.ItemCommandId];

        if (item == null)
        {
            // C38: dispositivo sorgente non risolto (ItemCommandId inesistente/rimosso):
            // segnala l'errore invece di chiudere il comando come se fosse andato a buon fine.
            navigatorCommand.ErrorDescription = $"Source device not found for ItemCommandId '{navigatorCommand.ItemCommandId}'";
            RecordNavigatorLog(null, navigatorCommand.ErrorDescription, LogEntryType.Warning);
            navigatorCommand.Status = Command.StatusTypes.Error;
            return;
        }

        if (PathDevices.Instance[navigatorCommand.ItemDestinationId] == null)
        {
            // Come per la sorgente: una destinazione che non esiste costruirebbe una missione che
            // nessun percorso puo' chiudere, e il comando risulterebbe eseguito. Si rifiuta.
            navigatorCommand.ErrorDescription = $"Destination device not found for ItemDestinationId '{navigatorCommand.ItemDestinationId}'";
            RecordNavigatorLog(item, navigatorCommand.ErrorDescription, LogEntryType.Warning);
            navigatorCommand.Status = Command.StatusTypes.Error;
            return;
        }

        if (item.LoadedLu != null)
        {
            navigatorCommand.ErrorDescription = $"Device {item.ASI}.{item.SUBITEM}.{item.PARTITION} already holds a load unit";
            RecordNavigatorLog(item, navigatorCommand.ErrorDescription, LogEntryType.Warning);
            navigatorCommand.Status = Command.StatusTypes.Error;
            return;
        }

        // Q1: la rotta di prova non ha un'unita' di carico reale (nessun LuId, nessun codice - vedi
        // il commento di testata), solo un tipo dettato dal comando: la modalita' di movimentazione
        // va chiesta per quel tipo, non da DescribeLoadUnitAsync che chiede per un'unita' di carico
        // che qui non esiste. Era DbInfo.GetHandlingMode, che su un guasto rispondeva zero; ora un
        // gestionale che non risponde solleva, e il comando si chiude in errore nel catch di
        // CommandToDo.Execute invece di costruire una missione con una modalita' inventata.
        var handlingMode = await RequireWarehouse($"GetLoadUnitTypeHandlingModeAsync sul tipo {navigatorCommand.LuTypeId}")
            .GetLoadUnitTypeHandlingModeAsync(navigatorCommand.LuTypeId);

        PathMission mission = new()
        {
            Date = DateTime.Now,
            SourceItemId = navigatorCommand.ItemCommandId,
            DestinationItemId = navigatorCommand.ItemDestinationId,
            PositionItemId = navigatorCommand.ItemCommandId,
            MissionType = Enums.MissionTypes.MovingToItem,
            OperationType = Enums.OperationTypes.Inbound,
            IdLuType = navigatorCommand.LuTypeId,
            HandlingMode = handlingMode?.HandlingMode ?? 0,
            StepOrigin = Enums.StepTypes.Automatic,
            IdCommandToDo = navigatorCommand.IdCommandToDo
        };

        ActiveMissions.Instance.Add(mission);
        await item.LoadLu(mission.MissionId);

        navigatorCommand.Status = Command.StatusTypes.Done;
    }

    /// <summary>
    /// Gestisce la movimentazione di un'unità di carico verso un dispositivo specifico.
    /// </summary>
    /// <param name="navigatorCommand">Il comando di navigazione contenente le informazioni necessarie</param>
    /// <param name="infoLog">Informazioni aggiuntive per il log</param>
    /// <remarks>
    /// Questo metodo verifica le condizioni necessarie per spostare un'unità di carico verso un dispositivo
    /// e avvia la procedura di movimentazione se possibile.
    /// </remarks>
    internal static async Task ManageMoveLuToItem(Command navigatorCommand, string infoLog)
    {
        if (PathDevices.Instance.Count == 0)
        {
            navigatorCommand.Status = Command.StatusTypes.Done;
            return;
        }

        var item = PathDevices.Instance[navigatorCommand.ItemCommandId];

        if (item == null)
        {
            // C38: dispositivo bersaglio non risolto (ItemCommandId inesistente/rimosso):
            // segnala l'errore invece di chiudere il comando come se fosse andato a buon fine.
            navigatorCommand.ErrorDescription = $"Target device not found for ItemCommandId '{navigatorCommand.ItemCommandId}'";
            RecordNavigatorLog(null, navigatorCommand.ErrorDescription, LogEntryType.Warning);
            navigatorCommand.Status = Command.StatusTypes.Error;
            return;
        }

        navigatorCommand.Status = Command.StatusTypes.Running;

        var idUdc = navigatorCommand.LuId;

        var destinationItem = PathDevices.Instance[navigatorCommand.ItemDestinationId];
        var positionItem = PathDevices.Instance[navigatorCommand.ItemCommandId];
        var mission = ActiveMissions.Instance.FirstOrDefault(x => x != null && x.LuId == idUdc);

        if (mission == null && destinationItem == null)
        {
            navigatorCommand.ErrorDescription = CommandMessages.strErrMoveLuMissionNotExist;
            navigatorCommand.Status = Command.StatusTypes.Error;
        }
        else if (await RequireWarehouse($"ManageMoveLuToItem sull'unita' di carico {idUdc}").DescribeLoadUnitAsync(idUdc) is null)
        {
            navigatorCommand.ErrorDescription = CommandMessages.strErrMoveLuLuNotExist;
            navigatorCommand.Status = Command.StatusTypes.Error;
        }
        else if (mission is
        {
            PositionItem.Status: not (Enums.DeviceStatusTypes.Ready or Enums.DeviceStatusTypes.Warning)
        })
        {
            navigatorCommand.ErrorDescription = CommandMessages.strErrMoveLuItemHoldLUNotReady;
            navigatorCommand.Status = Command.StatusTypes.Error;
        }
        else if (positionItem == null)
        {
            navigatorCommand.ErrorDescription = CommandMessages.strErrMoveLuMissionPositionNotExist;
            navigatorCommand.Status = Command.StatusTypes.Error;
        }
        else if (mission != null ||
                 (mission == null && destinationItem != null))
        {
            RecordNavigatorLog(positionItem,
                infoLog + $"Move Lu on Item {positionItem.ASI}.{positionItem.SUBITEM}.{positionItem.PARTITION}",
                LogEntryType.Information);
            await MoveLuToItem(navigatorCommand);
        }
        else
        {
            navigatorCommand.Status = Command.StatusTypes.Done;
        }
    }

    /// <summary>
    /// Gestisce la movimentazione manuale di un'unità di carico basata su un riferimento.
    /// </summary>
    /// <param name="navigatorCommand">Il comando di navigazione contenente le informazioni necessarie</param>
    /// <param name="infoLog">Informazioni aggiuntive per il log</param>
    /// <remarks>
    /// Questo metodo permette di movimentare un'unità di carico cercandola per riferimento anziché per ID.
    /// Utilizza un lock per garantire l'atomicità dell'operazione di estrazione.
    /// </remarks>
    internal static async Task ManageMoveReferenceManual(Command navigatorCommand, string infoLog)
    {
        if (PathDevices.Instance.Count == 0 || NavStatus != Enums.NavigatorStatusTypes.Run)
        {
            // R7: un comando che arriva qui NON ha fatto nulla - l'impianto non e' caricato oppure
            // il navigatore non e' in Run - e chiuderlo come Done diceva all'operatore che era
            // riuscito. Stessa forma applicata dalla voce C38 agli altri punti.
            navigatorCommand.ErrorDescription = PathDevices.Instance.Count == 0
                ? "No devices loaded: the plant diagram is not in memory"
                : $"Navigator is not running (status {NavStatus})";
            RecordNavigatorLog(null, navigatorCommand.ErrorDescription, LogEntryType.Warning);
            navigatorCommand.Status = Command.StatusTypes.Error;
            return;
        }

        var item = PathDevices.Instance[navigatorCommand.ItemCommandId];

        if (item == null)
        {
            navigatorCommand.Status = Command.StatusTypes.Done;
            return;
        }

        if (!await SyncExtraction.WaitAsync(SyncExtractionTimeout))
        {
            WriteLog("Timeout acquiring extraction lock in ManageMoveReferenceManual", LogEntryType.Warning);
            navigatorCommand.Status = Command.StatusTypes.Error;
            navigatorCommand.ErrorDescription = "Timeout acquiring extraction lock";
            return;
        }

        try
        {

            navigatorCommand.Status = Command.StatusTypes.Running;

            await DbInfo.GetItemId(navigatorCommand.StorageDoorCode);

            if (!string.IsNullOrEmpty(navigatorCommand.ReferenceId))
            {
                RecordNavigatorLog(item,
                    infoLog +
                    $"Move Reference {navigatorCommand.ReferenceId} Manually on Item {item.ASI}.{item.SUBITEM}.{item.PARTITION}",
                    LogEntryType.Information);
                await MoveReference(navigatorCommand, ExecuteMoveLuTypes.Manual);
            }
            else
            {
                navigatorCommand.Status = Command.StatusTypes.Done;
            }
        }
        finally
        {
            SyncExtraction.Release();
        }
    }

    /// <summary>
    /// Movimenta un'unità di carico verso una destinazione specifica.
    /// </summary>
    /// <param name="navigatorCommand">Il comando di navigazione contenente le informazioni necessarie</param>
    /// <param name="moveLuType">La modalità di esecuzione (automatica o manuale)</param>
    /// <remarks>
    /// Questo metodo verifica numerose condizioni prima di avviare la movimentazione, inclusi controlli
    /// sulla disponibilità dell'unità di carico, sulla validità delle posizioni e su possibili blocchi.
    /// </remarks>
    internal static async Task MoveLu(Command navigatorCommand, ExecuteMoveLuTypes moveLuType)
    {
        if (PathDevices.Instance.Count == 0 || NavStatus != Enums.NavigatorStatusTypes.Run)
        {
            // R7: un comando che arriva qui NON ha fatto nulla - l'impianto non e' caricato oppure
            // il navigatore non e' in Run - e chiuderlo come Done diceva all'operatore che era
            // riuscito. Stessa forma applicata dalla voce C38 agli altri punti.
            navigatorCommand.ErrorDescription = PathDevices.Instance.Count == 0
                ? "No devices loaded: the plant diagram is not in memory"
                : $"Navigator is not running (status {NavStatus})";
            RecordNavigatorLog(null, navigatorCommand.ErrorDescription, LogEntryType.Warning);
            navigatorCommand.Status = Command.StatusTypes.Error;
            return;
        }

        var idItem = await DbInfo.GetItemId(navigatorCommand.BufferCellId);

        var item = PathDevices.Instance[idItem];

        if (item == null)
        {
            navigatorCommand.Status = Command.StatusTypes.Done;
            return;
        }

        string errorDescription;
        // Compito 10: una domanda sola per tutta la durata del metodo - copriva anche il codice
        // (letto qui) e i dati dimensionali (letti piu' sotto, solo se si trova una posizione),
        // due chiamate separate allo stesso gestionale per lo stesso id.
        var warehouse = RequireWarehouse($"MoveLu sull'unita' di carico {navigatorCommand.LuId}");
        var loadUnitDescription = await warehouse.DescribeLoadUnitAsync(navigatorCommand.LuId);
        var luCode = loadUnitDescription?.Code;

        if (IsLuInExtraction(navigatorCommand.LuId) || await DbInfo.IsLuPresentInExtranctionLu(navigatorCommand.LuId))
        {
            errorDescription = string.Format(CommandMessages.strErrMoveLuInExtractQueue, luCode);
            RecordNavigatorLog(item, errorDescription, LogEntryType.Warning);
            await CommandToDoError(navigatorCommand, moveLuType, errorDescription);
            return;
        }

        // Compito 3: l'assegnazione di nota viene dal confine, sulla domanda che il gestionale
        // risponde sempre. Un guasto solleva - e il comando si chiude in errore nel catch per
        // comando di CommandToDo.Execute - invece di rispondere «libera», che era il difetto:
        // l'errore della lettura finiva nel registro e l'unita' di carico impegnata partiva lo
        // stesso. Non c'e' nessuna domanda in piu': e' lo stesso IWarehouse che il metodo ha gia'
        // preso per chiedere il codice dell'unita' di carico, e nessun gettone e' preso qui.
        if ((await warehouse.GetLoadUnitAssignmentStateAsync(navigatorCommand.LuId)).AssignedToNote)
        {
            errorDescription = string.Format(CommandMessages.strErrMoveLuLockedByNote, luCode);
            RecordNavigatorLog(item, errorDescription, LogEntryType.Warning);
            await CommandToDoError(navigatorCommand, moveLuType, errorDescription);
            return;
        }

        // Compito 2: le aree del polmone vengono dal confine. Un guasto solleva e il comando si
        // chiude in errore nel catch per comando di CommandToDo.Execute; «nessuna zona di prelievo»
        // qui sotto e' adesso solo un dato, e il messaggio che l'operatore legge dice il vero.
        var areasFilter = await warehouse.GetPickingBufferAreasAsync(navigatorCommand.BufferCellId);

        if (areasFilter.Count == 0)
        {
            errorDescription = string.Format(CommandMessages.strErrMoveLuNoPickingZoneLu, luCode);
            RecordNavigatorLog(item, errorDescription, LogEntryType.Warning);
            await CommandToDoError(navigatorCommand, moveLuType, errorDescription);
            return;
        }

        var luPosition = await warehouse.FindLoadUnitAsync(new FindLoadUnitRequest(
            navigatorCommand.LuId, areasFilter, null, LoadUnitLockFilter.Unlocked, CellLockFilter.FreeForUnloading));

        if (luPosition != null)
        {
            var unloadingDoorCode = await warehouse.ChoosePickUpDoorAsync(luPosition.LoadUnitId, luPosition.CellId);
            var sourceItemId = await DbInfo.GetItemId(unloadingDoorCode.DoorCode);
            var destinationItemId = await DbInfo.GetItemId(navigatorCommand.StorageDoorCode);

            var destinationItem = PathDevices.Instance[destinationItemId];
            var sourceItem = PathDevices.Instance[sourceItemId];

            if (sourceItemId == Guid.Empty || destinationItem == null || sourceItem == null)
            {
                errorDescription = string.Format(CommandMessages.strErrMoveLuSourceNoLink, unloadingDoorCode.DoorCode);
                RecordNavigatorLog(item, errorDescription, LogEntryType.Error);
                await CommandToDoError(navigatorCommand, moveLuType, errorDescription);
                return;
            }

            if (destinationItemId == Guid.Empty)
            {
                errorDescription =
                    string.Format(CommandMessages.strErrMoveLuDestNoLink, navigatorCommand.StorageDoorCode);
                RecordNavigatorLog(item, errorDescription, LogEntryType.Error);
                await CommandToDoError(navigatorCommand, moveLuType, errorDescription);
                return;
            }

            if (destinationItem.MaxUnloadWeight > 0 && loadUnitDescription is { Weight: > 0 } &&
                destinationItem.MaxUnloadWeight < loadUnitDescription.Weight)
            {
                errorDescription = string.Format(CommandMessages.strErrMoveLuExceedMaxWeight,
                    navigatorCommand.StorageDoorCode, destinationItem.MaxUnloadWeight.ToString(), loadUnitDescription.ToString());
                RecordNavigatorLog(item, errorDescription, LogEntryType.Error);
                await CommandToDoError(navigatorCommand, moveLuType, errorDescription);
                return;
            }

            if (sourceItem.DeviceType != Enums.DeviceTypes.Storage)
            {
                errorDescription = CommandMessages.strErrMoveLuNoStoreLocation;
                RecordNavigatorLog(item, errorDescription, LogEntryType.Warning);
                await CommandToDoError(navigatorCommand, moveLuType, errorDescription);
                return;
            }

            if (sourceItem.RelocateItemId == Guid.Empty || PathDevices.Instance[sourceItem.RelocateItemId] == null)
            {
                errorDescription = CommandMessages.strErrMoveLuStoreNoPickup;
                RecordNavigatorLog(item, errorDescription, LogEntryType.Error);
                await CommandToDoError(navigatorCommand, moveLuType, errorDescription);
                return;
            }

            if (sourceItem.DeviceType == Enums.DeviceTypes.Storage)
                sourceItem = PathDevices.Instance[sourceItem.RelocateItemId];

            if (sourceItem != null)
            {
                switch (sourceItem.DeviceType)
                {
                    case Enums.DeviceTypes.GroupOfAutomationDevices:
                        {
                            await BuildGroupedMoveLu(navigatorCommand.LuId, navigatorCommand.BufferCellId, sourceItemId,
                                destinationItemId, luPosition.CellId, unloadingDoorCode, navigatorCommand.WorkstationId, 0);
                            break;
                        }

                    default:
                        {
                            await BuildSingleMoveLu(navigatorCommand.LuId, navigatorCommand.BufferCellId, sourceItemId,
                                destinationItemId, luPosition.CellId, unloadingDoorCode, navigatorCommand.WorkstationId, 0);
                            break;
                        }
                }

                navigatorCommand.Status = Command.StatusTypes.Done;
            }
            else
            {
                errorDescription = CommandMessages.strErrMoveLuNoRelocationItem;
                RecordNavigatorLog(item, errorDescription, LogEntryType.Error);
                await CommandToDoError(navigatorCommand, moveLuType, errorDescription);
            }
        }
        else
        {
            errorDescription = string.Format(CommandMessages.strErrMoveLuNotFoundOrLocked,
                luCode, navigatorCommand.StorageDoorCode);
            RecordNavigatorLog(item, errorDescription, LogEntryType.Warning);
            await CommandToDoError(navigatorCommand, moveLuType, errorDescription);
        }
    }

    /// <summary>
    /// Imposta un dispositivo in stato online o offline.
    /// </summary>
    /// <param name="navigatorCommand">Il comando di navigazione contenente le informazioni necessarie</param>
    /// <param name="statusOffLine">True per impostare il dispositivo offline, False per impostarlo online</param>
    /// <remarks>
    /// Questo metodo modifica lo stato operativo di un dispositivo, permettendo di escluderlo temporaneamente 
    /// dal sistema di automazione (offline) o di reinserirlo (online).
    /// </remarks>
    internal static async Task SetItemOffOnLine(Command navigatorCommand, bool statusOffLine)
    {
        if (PathDevices.Instance.Count == 0)
        {
            navigatorCommand.Status = Command.StatusTypes.Done;
            return;
        }

        var item = PathDevices.Instance[navigatorCommand.ItemCommandId];

        if (item == null)
        {
            // C38: dispositivo bersaglio non risolto (ItemCommandId inesistente/rimosso):
            // segnala l'errore invece di chiudere il comando come se fosse andato a buon fine.
            navigatorCommand.ErrorDescription = $"Target device not found for ItemCommandId '{navigatorCommand.ItemCommandId}'";
            RecordNavigatorLog(null, navigatorCommand.ErrorDescription, LogEntryType.Warning);
            navigatorCommand.Status = Command.StatusTypes.Error;
            return;
        }

        await item.SetStatusOffLine(statusOffLine);
    }

    #endregion Internal Methods

}
