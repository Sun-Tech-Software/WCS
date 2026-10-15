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

    #region Private Methods

    /// <summary>
    /// Gestisce gli errori nei comandi, aggiornando lo stato e registrando l'errore nel log.
    /// </summary>
    /// <param name="navigatorCommand">Il comando che ha riscontrato l'errore</param>
    /// <param name="moveLuType">La modalità di esecuzione (automatica o manuale)</param>
    /// <param name="errorDescription">La descrizione dell'errore</param>
    /// <exception cref="ArgumentOutOfRangeException">Lanciata se moveLuType non è un valore valido dell'enumerazione</exception>
    /// <remarks>
    /// In modalità manuale, imposta lo stato del comando a Error e registra la descrizione dell'errore.
    /// In modalità automatica, reimposta lo stato delle operazioni e segna il comando come completato.
    /// </remarks>
    private static async Task CommandToDoError(Command navigatorCommand, ExecuteMoveLuTypes moveLuType,
        string errorDescription)
    {
        // Evita di chiamare RecordNavigatorLog due volte
        switch (moveLuType)
        {
            case ExecuteMoveLuTypes.Manual:
                navigatorCommand.ErrorDescription = errorDescription;
                navigatorCommand.Status = Command.StatusTypes.Error;
                break;
            case ExecuteMoveLuTypes.Automatic:
                await ResetCellOperationsStatus(navigatorCommand.LuId);
                navigatorCommand.Status = Command.StatusTypes.Done;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(moveLuType), moveLuType, null);
        }

        RecordNavigatorLog(null, errorDescription, LogEntryType.Error);
    }

    /// <summary>
    /// Svuota logicamente una cella del magazzino, preparando la movimentazione delle unità di carico contenute.
    /// </summary>
    /// <param name="sourceDoorCode">Codice della porta di origine</param>
    /// <param name="destinationDoorCode">Codice della porta di destinazione</param>
    /// <param name="idWorkstation">ID della workstation associata all'operazione</param>
    /// <remarks>
    /// Questo metodo esegue numerose verifiche prima di pianificare lo svuotamento della cella,
    /// creando le necessarie richieste di estrazione nel sistema.
    /// </remarks>
    private static async Task<string> EmptyCellLogically(string? sourceDoorCode, string? destinationDoorCode, string? idWorkstation)
    {
        var errorResult = string.Empty;

        if (string.IsNullOrEmpty(sourceDoorCode) || string.IsNullOrEmpty(destinationDoorCode) ||
            string.IsNullOrEmpty(idWorkstation))
        {
            errorResult = CommandMessages.strErrMoveLuDataEmpty;
            return errorResult;
        }

        // Compito 9: ex WmsBusinessArea.CelleMagazzino.GetDatiCella(doorCode), ora
        // IWarehouse.DescribeCellsByDoorAsync - qui si usa solo .CellId, come gia' faceva questo
        // chiamante con .FirstOrDefault() sul risultato originale.
        var source = (await RequireWarehouse($"DescribeCellsByDoorAsync sulla porta {sourceDoorCode}")
            .DescribeCellsByDoorAsync(sourceDoorCode)).FirstOrDefault();
        var destination = (await RequireWarehouse($"DescribeCellsByDoorAsync sulla porta {destinationDoorCode}")
            .DescribeCellsByDoorAsync(destinationDoorCode)).FirstOrDefault();

        if (source == null || destination == null)
        {
            errorResult = string.Format(CommandMessages.strErrMoveLuItemError);
            return errorResult;
        }

        if (destination.CellId == 0)
            errorResult = string.Format(CommandMessages.strErrMoveLuDestNotExsits, sourceDoorCode);
        else if (source.CellId == 0)
            errorResult = string.Format(CommandMessages.strErrMoveLuSourceNotExsits, sourceDoorCode);

        if (!string.IsNullOrEmpty(errorResult))
            return errorResult;

        var sourceItemId = await DbInfo.GetItemId(sourceDoorCode);
        var sourceItem = PathDevices.Instance[sourceItemId];
        var destinationItemId = await DbInfo.GetItemId(destinationDoorCode);
        var askingItem = PathDevices.Instance[destinationItemId];

        if (sourceItemId == Guid.Empty)
        {
            errorResult = string.Format(CommandMessages.strErrMoveLuSourceNoLink, sourceDoorCode);
            RecordNavigatorLog(askingItem, errorResult, LogEntryType.Error);
            return errorResult;
        }

        if (destinationItemId == Guid.Empty)
        {
            errorResult = string.Format(CommandMessages.strErrMoveLuDestNoLink, destinationDoorCode);
            RecordNavigatorLog(askingItem, errorResult, LogEntryType.Error);
            return errorResult;
        }

        if (sourceItem == null || sourceItem.DeviceType != Enums.DeviceTypes.Storage)
        {
            errorResult = CommandMessages.strErrMoveLuNoStoreLocation;
            RecordNavigatorLog(askingItem, errorResult, LogEntryType.Warning);
            return errorResult;
        }

        if (sourceItem.RelocateItemId == Guid.Empty || PathDevices.Instance[sourceItem.RelocateItemId] == null)
        {
            errorResult = CommandMessages.strErrMoveLuStoreNoPickup;
            RecordNavigatorLog(askingItem, errorResult, LogEntryType.Error);
            return errorResult;
        }

        var automationItem = PathDevices.Instance[sourceItemId];
        if (automationItem == null) return errorResult;

        if (automationItem.DeviceType == Enums.DeviceTypes.Storage)
            automationItem = PathDevices.Instance[automationItem.RelocateItemId];

        if (automationItem == null) return errorResult;

        // Compito 16: ex OperazioniUdc.UdcCella(cella, TipiPorta.A), di cui questo ciclo filtrava via
        // le righe assegnate. IWarehouse.GetCellContentAsync legge gia' le sole posizioni non
        // assegnate, ordinate per posizione, quindi il filtro sparisce insieme alla colonna; la
        // porta smette di essere un parametro (ordinava al contrario solo per la porta B, e qui si
        // passava la A) e il campo «unita' di carico virtuale» che veniva insieme non lo leggeva
        // nessuno.
        var lUtoDownload = await RequireWarehouse($"GetCellContentAsync sulla cella {source.CellId}")
            .GetCellContentAsync(source.CellId);

        try
        {
            foreach (var luId in lUtoDownload)
                if (IsLuInExtraction(luId))
                {
                }
                else if (await DbInfo.IsLuPresentInExtranctionLu(luId))
                {
                    await DbInfo.UpdateExtractionLu(luId, destination.CellId, 0);
                }
                else
                {
                    var row = new ExtractionQueueItem
                    {
                        BufferCellId = destination.CellId,
                        DestinationItemId = destinationItemId,
                        SourceItemId = sourceItemId,
                        RelocatorItemId = automationItem.ItemGuid,
                        WorkstationId = idWorkstation,
                        LoadUnitId = luId,
                        CellId = source.CellId,
                        DoorCode = sourceDoorCode,
                        Extract = true,
                        Processed = false
                    };

                    await ExtractionQueue.UpsertAsync(row);
                }
        }
        catch (QueuedWriteRejectedException ex)
        {
            // La riga di coda non e' stata scritta: lo scrittore si sta fermando, o la coda e'
            // piena. Prima la scrittura non era attesa (`_ =`) e il rifiuto non lo guardava
            // nessuno, quindi lo svuotamento della cella si dichiarava riuscito con la coda che
            // poteva essere vuota. Adesso il chiamante riceve la descrizione dell'errore.
            errorResult = $"{CommandMessages.strErrMoveLuItemError} - {ex.Message}";
            RecordNavigatorLog(askingItem, errorResult, LogEntryType.Error);
        }

        return errorResult;
    }

    /// <summary>
    /// Svuota fisicamente una cella del magazzino, avviando la movimentazione delle unità di carico.
    /// </summary>
    /// <param name="sourceDoorCode">Codice della porta di origine</param>
    /// <param name="destinationDoorCode">Codice della porta di destinazione</param>
    /// <param name="idWorkstation">ID della workstation associata all'operazione</param>
    /// <param name="idLuType">ID del tipo di unità di carico</param>
    /// <param name="errorResult">Output: descrizione dell'eventuale errore riscontrato</param>
    /// <remarks>
    /// Questo metodo verifica la disponibilità della cella e dei dispositivi associati prima di
    /// avviare la missione di svuotamento fisico.
    /// </remarks>
    private static async Task<string> EmptyCellPisically(string? sourceDoorCode, string? destinationDoorCode, string? idWorkstation,
        decimal idLuType)
    {
        var errorResult = string.Empty;

        if (string.IsNullOrEmpty(sourceDoorCode) || string.IsNullOrEmpty(destinationDoorCode) ||
            string.IsNullOrEmpty(idWorkstation))
        {
            errorResult = CommandMessages.strErrMoveLuDataEmpty;
            return errorResult;
        }

        errorResult = string.Empty;

        // Compito 9: ex WmsBusinessArea.CelleMagazzino.GetDatiCella(doorCode), ora
        // IWarehouse.DescribeCellsByDoorAsync - qui si usa solo .CellId, come gia' faceva questo
        // chiamante con .FirstOrDefault() sul risultato originale.
        var source = (await RequireWarehouse($"DescribeCellsByDoorAsync sulla porta {sourceDoorCode}")
            .DescribeCellsByDoorAsync(sourceDoorCode)).FirstOrDefault();
        var destination = (await RequireWarehouse($"DescribeCellsByDoorAsync sulla porta {destinationDoorCode}")
            .DescribeCellsByDoorAsync(destinationDoorCode)).FirstOrDefault();

        if (source == null || destination == null) return errorResult;

        if (destination.CellId == 0)
            errorResult = string.Format(CommandMessages.strErrMoveLuDestNotExsits, sourceDoorCode);
        else if (source.CellId == 0)
            errorResult = string.Format(CommandMessages.strErrMoveLuSourceNotExsits, sourceDoorCode);

        if (!string.IsNullOrEmpty(errorResult))
            return errorResult;

        var sourceItemId = await DbInfo.GetItemId(sourceDoorCode);
        var destinationItemId = await DbInfo.GetItemId(destinationDoorCode);
        var askingItem = PathDevices.Instance[destinationItemId];

        if (sourceItemId == Guid.Empty)
        {
            errorResult = string.Format(CommandMessages.strErrMoveLuSourceNoLink, sourceDoorCode);
            RecordNavigatorLog(askingItem, errorResult, LogEntryType.Error);
            return errorResult;
        }

        if (destinationItemId == Guid.Empty)
        {
            errorResult = string.Format(CommandMessages.strErrMoveLuDestNoLink, destinationDoorCode);
            RecordNavigatorLog(askingItem, errorResult, LogEntryType.Error);
            return errorResult;
        }

        var sourceItem = PathDevices.Instance[sourceItemId];

        if (sourceItem == null || sourceItem.DeviceType != Enums.DeviceTypes.Storage)
        {
            errorResult = CommandMessages.strErrMoveLuNoStoreLocation;
            RecordNavigatorLog(askingItem, errorResult, LogEntryType.Warning);
            return errorResult;
        }

        if (sourceItem.RelocateItemId == Guid.Empty || PathDevices.Instance[sourceItem.RelocateItemId] == null)
        {
            errorResult = CommandMessages.strErrMoveLuStoreNoPickup;
            RecordNavigatorLog(askingItem, errorResult, LogEntryType.Error);
            return errorResult;
        }

        // Compito 18: ex OperazioniUdc.UdcCella(cella, TipiPorta.A), ora
        // IWarehouse.GetCellPositionsAsync. Non e' GetCellContentAsync poco piu' su in questo stesso
        // file (svuotamento logico): quella risponde le sole posizioni non prenotate, qui servono
        // tutte, con lo stato di prenotazione, perche' e' proprio su quello che si decide. La porta
        // smette di essere un parametro (ordinava al contrario solo per la porta B, e qui si passava
        // la A) e il campo «unita' di carico virtuale» che veniva insieme non lo leggeva nessuno.
        var cellPositions = await RequireWarehouse($"GetCellPositionsAsync sulla cella {source.CellId}")
            .GetCellPositionsAsync(source.CellId);

        // Migrato intatto, compresa la cella vuota: All su un elenco vuoto e' vero, quindi una cella
        // senza posizioni rifiuta il comando con questo stesso messaggio, come faceva prima.
        if (cellPositions.All(x => x.Reserved))
        {
            errorResult = $"The requested cell {sourceDoorCode} have LU on it";
            RecordNavigatorLog(askingItem, errorResult, LogEntryType.Warning);
            return errorResult;
        }

        if (cellPositions.Any(x => x.Reserved))
        {
            var reservedLoadUnits = cellPositions.Where(x => x.Reserved).Select(x => x.LoadUnitId).ToList();

            // Il passo che resta di qua: le missioni attive sono del motore, il gestionale non ha il
            // dato e non potrebbe deciderlo. Stessa divisione del quarto motivo di rifiuto delle note
            // di spedizione (RejectNoteAssignmentAsync): il fatto lo scopre il WCS, la conseguenza
            // sui dati del magazzino la esegue il gestionale.
            if (ActiveMissions.Instance.Where(x => x != null && reservedLoadUnits.Contains(x.LuId)).ToList().Count == 0)
            {
                // Compito 18: ex OperazioniUdc.DissociaDaOperatore(cella). Cancella tutte le
                // posizioni della cella, prenotate e fisiche - il nome nuovo dice quello che fa.
                await RequireWarehouse($"ClearCellPositionsAsync sulla cella {source.CellId}")
                    .ClearCellPositionsAsync(source.CellId);
            }
            else
            {
                errorResult = $"Into the requested cell {sourceDoorCode} is stored a LU, the process can't start";
                RecordNavigatorLog(askingItem, errorResult, LogEntryType.Warning);
                return errorResult;
            }
        }

        var automationItem = PathDevices.Instance[sourceItemId];

        if (automationItem == null) return errorResult;

        if (automationItem.DeviceType == Enums.DeviceTypes.Storage)
            automationItem = PathDevices.Instance[automationItem.RelocateItemId];

        if (automationItem == null) return errorResult;

        if (automationItem.EmptyCellStatus == Enums.EmptyCellStatusTypes.Active &&
            !string.IsNullOrEmpty(automationItem.EmptyCellSourceDoor))
        {
            errorResult =
                $"The Item linked to the requested cell {sourceDoorCode} is active for a different emptying cell process";
            RecordNavigatorLog(askingItem, errorResult, LogEntryType.Warning);
            return errorResult;
        }

        await automationItem.AddMissionToEmptyCell(sourceDoorCode, destinationDoorCode, idWorkstation, idLuType);

        return errorResult;
    }

    /// <summary>
    /// Gestisce lo spostamento di un'unità di carico (LU) verso un dispositivo specifico identificato nel comando.
    /// </summary>
    /// <param name="navigatorCommand">Il comando di navigazione contenente tutte le informazioni necessarie per la movimentazione</param>
    /// <remarks>
    /// Questo metodo verifica prima se il navigatore è in esecuzione, poi cerca una missione esistente per l'unità di carico
    /// o ne crea una nuova se necessario. Verifica se il dispositivo può accettare l'unità di carico e, in base allo stato attuale,
    /// aggiorna o crea la missione appropriata. Gestisce sia il caso in cui l'unità non è ancora posizionata su un dispositivo
    /// sia il caso in cui deve essere spostata da un dispositivo a un altro.
    /// </remarks>
    private static async Task MoveLuToItem(Command navigatorCommand)
    {
        var idUdc = navigatorCommand.LuId;

        var destinationItem = PathDevices.Instance[navigatorCommand.ItemDestinationId];
        var positionItem = PathDevices.Instance[navigatorCommand.ItemCommandId];

        if (NavStatus != Enums.NavigatorStatusTypes.Run)
        {
            navigatorCommand.ErrorDescription = CommandMessages.strErrMoveLuNavNotRunMode;
            navigatorCommand.Status = Command.StatusTypes.Error;
            RecordNavigatorLog(positionItem, navigatorCommand.ErrorDescription, LogEntryType.Warning);
            return;
        }

        var newMission = ActiveMissions.Instance.FirstOrDefault(x => x != null && x.LuId == idUdc);

        if (newMission == null)
        {
            // Compito 10: punto di costruzione non fra i quattro ufficiali del brief (trovato
            // cercando per nome). Il tipo arriva dal comando, non dalla descrizione del gestionale
            // (vedi il commento sotto su IdLuType, invariato) - quindi la modalita' di movimentazione,
            // che dipende dal tipo, va chiesta per il tipo del comando (chiave luTypeId, non luId:
            // non e' la stessa domanda di DescribeLoadUnitAsync, e ha un'operazione sua al confine).
            // Codice e tara vengono invece dall'unita' di carico reale, quindi da una sola
            // DescribeLoadUnitAsync.
            var loadUnitDescription = navigatorCommand.LuId > 0
                ? await RequireWarehouse($"MoveLuToItem sull'unita' di carico {navigatorCommand.LuId}").DescribeLoadUnitAsync(navigatorCommand.LuId)
                : null;

            // Q1: era DbInfo.GetHandlingMode, zero su un guasto. Ora solleva, e il comando si chiude
            // in errore nel catch di CommandToDo.Execute.
            var handlingMode = await RequireWarehouse($"GetLoadUnitTypeHandlingModeAsync sul tipo {navigatorCommand.LuTypeId}")
                .GetLoadUnitTypeHandlingModeAsync(navigatorCommand.LuTypeId);

            newMission = new PathMission
            {
                LuId = navigatorCommand.LuId,
                SourceItemId = navigatorCommand.ItemCommandId,
                DestinationItem = destinationItem,
                StorageDoorCode = string.Empty,
                IdCommandToDo = navigatorCommand.IdCommandToDo,
                LuCode = loadUnitDescription?.Code ?? string.Empty,
                Date = DateTime.Now,
                // Il tipo di UDC arriva dal comando: senza di lui la missione nasce con IdLuType a zero e
                // le strategie che allocano per famiglia (BuildRouteToStoreByIdLuType) non trovano nulla.
                IdLuType = navigatorCommand.LuTypeId,
                HandlingMode = handlingMode?.HandlingMode ?? 0,
                Tare = loadUnitDescription?.Tare ?? 0,
                OperationType = destinationItem == null ? Enums.OperationTypes.Inbound : Enums.OperationTypes.Outbound,
                StepOrigin = Enums.StepTypes.Manual
            };
        }

        if (newMission.PositionItem != null && newMission.PositionItem != positionItem && positionItem is
            {
                LuManager: not null
            } &&
            !positionItem.LuManager.CanAcceptLu(newMission))
        {
            navigatorCommand.ErrorDescription = CommandMessages.strErrMoveLuLoadLuItemNotReady;
            navigatorCommand.Status = Command.StatusTypes.Error;
            RecordNavigatorLog(positionItem, navigatorCommand.ErrorDescription, LogEntryType.Warning);
            return;
        }

        if (newMission.PositionItem == null && positionItem != null)
        {
            ActiveMissions.Instance.Add(newMission);
            await positionItem.LoadLu(newMission.MissionId);
        }
        else
        {
            var lastPositionItem = newMission.PositionItem;

            // La destinazione, la rimozione della rotta e la posizione in un colpo solo, sotto il
            // lucchetto della rotta (PathMission.MoveTo, voce 41): prima erano tre scritture separate,
            // e uno scrittore della rotta che passava fra la rimozione e la posizione lasciava alla
            // missione spostata la rotta dal posto vecchio. Come lo spostamento dell'operatore, il
            // comando non confronta la versione e vince; la rotta la ricostruisce chi esegue dopo.
            // Senza dispositivo la posizione resta quella che e', come prima (il setter non la
            // cambiava). Uno step in volo il comando non lo toglie (risposta 1 del proprietario
            // dell'11/10, come la decisione del 3/9 e come lo spostamento dell'operatore): lo sguardo
            // sta dentro lo stesso lucchetto, prima della destinazione, e con lo step in volo non si
            // scrive niente; il comando si chiude in errore con la riga, e l'operatore lo rimanda quando
            // il movimento e' finito. Prima toglieva lo step in volo, liberando a meta' movimento chi
            // lo eseguiva.
            MissionStep? inFlight = null;

            var moved = newMission.MoveTo(positionItem?.ItemGuid ?? newMission.PositionItemId, () =>
            {
                inFlight = newMission.StepInFlight();
                if (inFlight != null)
                    return false;

                if (destinationItem != null)
                    newMission.DestinationItem = destinationItem;
                return true;
            });

            if (moved != PathMission.RouteWriteOutcome.Written)
            {
                navigatorCommand.ErrorDescription =
                    $"Load unit {idUdc} not moved: its step {inFlight?.StepId} is in flight from {inFlight?.FromNodeId}, retry when the movement is over";
                navigatorCommand.Status = Command.StatusTypes.Error;
                RecordNavigatorLog(positionItem, navigatorCommand.ErrorDescription, LogEntryType.Warning);
                return;
            }

            // Lanciate senza attesa, come prima, ma tenute da BackgroundWork: chi deve sapere quando il
            // lavoro di sfondo e' finito (le prove, prima di smontare l'impianto) le vede.
            if (positionItem != null) _ = BackgroundWork.Track(positionItem.ExecuteStep(1000));
            if (lastPositionItem != null) _ = BackgroundWork.Track(lastPositionItem.ExecuteStep(1000));
        }

        navigatorCommand.Status = Command.StatusTypes.Done;
    }

    /// <summary>
    /// Gestisce lo spostamento di un'unità di carico basandosi su un riferimento anziché sull'ID dell'unità.
    /// </summary>
    /// <param name="navigatorCommand">Il comando di navigazione contenente il riferimento e le altre informazioni necessarie</param>
    /// <param name="moveLuType">La modalità di esecuzione (automatica o manuale)</param>
    /// <remarks>
    /// Questo metodo ricerca un'unità di carico basandosi sul riferimento specificato nel comando, applicando filtri
    /// per le aree di prelievo disponibili. Se trova l'unità, verifica una serie di condizioni di validità come
    /// l'esistenza dei dispositivi sorgente e destinazione, i limiti di peso, e la compatibilità dei tipi di dispositivo.
    /// In base al tipo di dispositivo sorgente, crea una richiesta di movimentazione raggruppata o singola.
    /// Gestisce tutti gli errori possibili e aggiorna lo stato del comando di conseguenza.
    /// </remarks>
    private static async Task MoveReference(Command navigatorCommand, ExecuteMoveLuTypes moveLuType)
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
            // C38: dispositivo bersaglio non risolto (BufferCellId inesistente o rimosso):
            // segnala l'errore invece di chiudere il comando come se fosse andato a buon fine.
            navigatorCommand.ErrorDescription = $"Target device not found for BufferCellId '{navigatorCommand.BufferCellId}'";
            RecordNavigatorLog(null, navigatorCommand.ErrorDescription, LogEntryType.Warning);
            navigatorCommand.Status = Command.StatusTypes.Error;
            return;
        }

        string errorDescription;

        // Compito 20: ex la lettura di DbInfo.PickingBufferAreas piu' le opzioni di ricerca piu'
        // WmsBusinessArea.CelleMagazzino.TrovaReferenza, di cui si prendeva il primo risultato. La
        // domanda che parte di qua porta i due dati che il motore sa - la referenza chiesta
        // dall'operatore e il polmone - e tutto il resto si formula di la': le opzioni di ricerca
        // erano valori fissi presi dal modello del gestionale, e le aree servite dal polmone erano
        // una lettura del motore su una tabella del gestionale. I due rifiuti restano due, con i
        // loro due messaggi, perche' l'esito viaggia nella risposta.
        var referenceSearch = await RequireWarehouse($"MoveReference sulla referenza {navigatorCommand.ReferenceId}")
            .FindLoadUnitByReferenceAsync(new FindLoadUnitByReferenceRequest(
                navigatorCommand.ReferenceId ?? string.Empty, navigatorCommand.BufferCellId));

        if (referenceSearch.Outcome == ReferenceSearchOutcome.NoPickingAreas)
        {
            errorDescription = string.Format(CommandMessages.strErrMoveLuNoPickingZoneReference,
                navigatorCommand.ReferenceId);
            RecordNavigatorLog(item, errorDescription, LogEntryType.Warning);
            await CommandToDoError(navigatorCommand, moveLuType, errorDescription);
            return;
        }

        if (referenceSearch.Outcome == ReferenceSearchOutcome.Found)
        {
            var warehouse = RequireWarehouse($"MoveReference sull'unita' di carico {referenceSearch.LoadUnitId}");
            var unloadingDoorCode = await warehouse.ChoosePickUpDoorAsync(referenceSearch.LoadUnitId, referenceSearch.CellId);
            var luWeight = await warehouse.DescribeLoadUnitAsync(referenceSearch.LoadUnitId);
            var sourceItemId = await DbInfo.GetItemId(unloadingDoorCode.DoorCode);
            var destinationItemId = await DbInfo.GetItemId(navigatorCommand.StorageDoorCode);
            var sourceItem = PathDevices.Instance[sourceItemId];
            var destinationItem = PathDevices.Instance[destinationItemId];

            if (sourceItemId == Guid.Empty || sourceItem == null || destinationItem == null)
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

            if (destinationItem.MaxUnloadWeight > 0 && luWeight is { Weight: > 0 } &&
                destinationItem.MaxUnloadWeight < luWeight.Weight)
            {
                errorDescription = string.Format(CommandMessages.strErrMoveLuExceedMaxWeight,
                    navigatorCommand.StorageDoorCode, destinationItem.MaxUnloadWeight.ToString(),
                    luWeight.ToString());
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
                            await BuildGroupedMoveLu(referenceSearch.LoadUnitId, navigatorCommand.BufferCellId, sourceItemId,
                                destinationItemId, referenceSearch.CellId, unloadingDoorCode, navigatorCommand.WorkstationId, 0);
                            break;
                        }

                    default:
                        {
                            await BuildSingleMoveLu(referenceSearch.LoadUnitId, navigatorCommand.BufferCellId, sourceItemId,
                                destinationItemId, referenceSearch.CellId, unloadingDoorCode, navigatorCommand.WorkstationId, 0);
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
                (await RequireWarehouse($"MoveReference sull'unita' di carico {navigatorCommand.LuId}").DescribeLoadUnitAsync(navigatorCommand.LuId))?.Code,
                navigatorCommand.StorageDoorCode);
            RecordNavigatorLog(item, errorDescription, LogEntryType.Warning);
            await CommandToDoError(navigatorCommand, moveLuType, errorDescription);
        }
    }

    #endregion Private Methods

}
