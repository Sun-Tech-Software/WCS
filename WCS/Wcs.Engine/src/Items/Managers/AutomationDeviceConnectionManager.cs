using System.Globalization;
using Wcs.Engine.ORM;
using Log.Interface;
using Wcs.Connection.Interface.Events;
using Wcs.Engine.Api;
using Wcs.Wms.Contracts;
using static Wcs.Common.Enums;
using static Wcs.Engine.Items.AutomationDevice;
using static Wcs.Engine.Items.PathMission;

namespace Wcs.Engine.Items.Managers;

/// <summary>
/// Gestisce la connessione e gli eventi di comunicazione per i dispositivi di automazione.
/// Fornisce funzionalità per la gestione degli eventi ASI, il controllo dei movimenti completati,
/// l'invio di dati LU e la gestione di nuove unità di carico sui dispositivi ASI.
/// </summary>
/// <param name="device">Il dispositivo di automazione associato a questo manager di connessione</param>
public class AutomationDeviceConnectionManager(AutomationDevice device)
{

    #region Public Methods

    /// <summary>
    /// Gestisce gli eventi ASI generici provenienti dal dispositivo di automazione.
    /// </summary>
    /// <param name="e">Gli argomenti dell'evento ASI contenenti le informazioni dell'evento</param>
    public void AsiEvent(AsiEventEventArgs? e)
    {
        // Implementazione del metodo ConnectionAsiEvent originale
    }

    #endregion Public Methods

    #region Internal Methods

    /// <summary>
    /// Gestisce il completamento di un movimento ASI, processando i risultati e gestendo
    /// diversi tipi di errore o successo dell'operazione di movimento.
    /// </summary>
    /// <param name="e">Gli argomenti dell'evento contenenti il risultato del movimento completato</param>
    internal async Task AsiMovementIsCompleted(AsiMovementIsCompletedEventArgs? e)
    {
        try
        {
            if (e == null) return;
            if (IsUnknownSingleStepResult(e)) return;

            switch (e.Result)
            {
                case ResultCodeTypes.Success:
                    await HandleMovementSuccess(e);
                    break;

                case ResultCodeTypes.Error:
                    await HandleMovementError(e);
                    break;

                case ResultCodeTypes.TimeOut:
                case ResultCodeTypes.ConnectionError:
                    await HandleMovementTimeoutOrConnectionError(e);
                    break;

                case ResultCodeTypes.None:
                default:
                    break;
            }
        }
        catch (Exception ex)
        {
            device.LogError(ex);
        }
    }

    /// <summary>
    /// Gestisce l'invio di dati LU al dispositivo ASI, processando la risposta
    /// e gestendo i timeout o errori di connessione.
    /// </summary>
    /// <param name="e">Gli argomenti dell'evento contenenti il risultato dell'invio dati LU</param>
    internal async Task AsiSendLuData(AsiSendLuDataEventArgs? e)
    {
        if (device.LoadedLu == null || e == null)
            return;

        switch (e.Result)
        {
            case ResultCodeTypes.TimeOut:
            case ResultCodeTypes.ConnectionError:
                await HandleSendLuDataTimeoutOrConnectionError(e);
                break;

            case ResultCodeTypes.Success:
                await HandleSendLuDataSuccess(e);
                break;

            case ResultCodeTypes.None:
            case ResultCodeTypes.Error:
            default:
                break;
        }

        if (device.AutoExecuteStep)
            _ = device.ExecuteStep();
    }

    /// <summary>
    /// Gestisce la rilevazione di una nuova unità di carico (LU) sul dispositivo ASI,
    /// creando una nuova missione di percorso e inizializzando il processo di caricamento.
    /// </summary>
    /// <param name="e">Gli argomenti dell'evento contenenti i dati della nuova LU rilevata</param>
    internal async Task NewLuOnASI(NewLuOnAsiEventArgs? e)
    {
        if (device.LoadedLu != null || device.IsLoadingLu)
            return;

        try
        {
            device.IsLoadingLu = true;

            var newMission = new PathMission
            {
                Date = DateTime.Now,
                SourceItemId = device.ItemGuid
            };

            if (e?.Data != null)
            {
                // Compito 10: punto di costruzione "nel gestore delle connessioni". Il ramo col
                // codice risolve prima l'identificativo (ResolveLoadUnitByCodeAsync), poi chiede
                // codice/tipo/modalita'/tara con la stessa domanda del ramo con l'id gia' noto
                // (DescribeLoadUnitAsync) - due chiamate invece di una perche' senza id non si puo'
                // ancora chiedere la descrizione. Il ramo con l'id gia' noto resta a una sola.
                if (!string.IsNullOrWhiteSpace(e.Data.LuCode))
                {
                    var code = e.Data.LuCode.Trim();
                    newMission.LuCode = code;
                    var identity = await Navigator.RequireWarehouse($"NewLuOnASI sul codice {code}").ResolveLoadUnitByCodeAsync(code);
                    newMission.LuId = identity?.LoadUnitId ?? 0;
                }
                else if (e.Data.LuId > 0)
                {
                    newMission.LuId = e.Data.LuId;
                }

                if (newMission.LuId > 0)
                {
                    var loadUnitDescription = await Navigator.RequireWarehouse($"NewLuOnASI sull'unita' di carico {newMission.LuId}")
                        .DescribeLoadUnitAsync(newMission.LuId);
                    newMission.LuCode = loadUnitDescription?.Code ?? newMission.LuCode;
                    newMission.IdLuType = loadUnitDescription?.LoadUnitTypeId ?? 0;
                    newMission.HandlingMode = (int)Math.Round(loadUnitDescription?.HandlingMode ?? 0);
                    newMission.Tare = loadUnitDescription?.Tare ?? 0;
                }
            }

            newMission.OperationType = OperationTypes.Inbound;
            newMission.StepOrigin = StepTypes.Automatic;

            if (device.LuManager?.CanAcceptLu(newMission) == false) return;

            Collections.ActiveMissions.Instance.Add(newMission);
            await device.LoadLu(newMission.MissionId);
        }
        finally
        {
            device.IsLoadingLu = false;
        }
    }

    #endregion Internal Methods

    #region Private Methods

    /// <summary>
    /// Rejects a single-device PLC result whose supplied step is no longer present.
    /// </summary>
    private bool IsUnknownSingleStepResult(AsiMovementIsCompletedEventArgs e)
    {
        var isGroupDevice = device.DeviceType == DeviceTypes.GroupOfAutomationDevices || device.GroupItemId != 0;
        if (isGroupDevice || e.Result is not (ResultCodeTypes.Success or ResultCodeTypes.Error or
            ResultCodeTypes.TimeOut or ResultCodeTypes.ConnectionError))
            return false;

        if (Collections.MissionSteps.Instance.FirstOrDefault(x => x.StepId == e.StepGuid) != null)
            return false;

        Navigator.WriteLog(
            $"AsiMovementIsCompleted {device.ItemGuid}: step {e.StepGuid} sconosciuto per risultato {e.Result}, nessuno step selezionato",
            LogEntryType.Warning);
        return true;
    }

    /// <summary>
    /// Gli step del movimento di gruppo con quel gettone, o <c>null</c> se il gettone e' vuoto.
    /// </summary>
    /// <param name="groupedMovementId">Il gettone rimandato dal campo</param>
    /// <param name="origin">Il gestore che chiede, per la riga di log</param>
    /// <remarks>
    /// <c>Guid.Empty</c> non e' il gettone di nessun gruppo: e' il <c>GroupedMovementId</c> di tutti
    /// gli step senza gruppo dell'impianto, e un filtro che lo accettasse li prenderebbe tutti — ne
    /// rilascerebbe le destinazioni o li marcherebbe in errore (il 137 li toglieva anche, prima
    /// dell'8/10). Si scrive una riga, perche' un gettone vuoto dal campo vuol dire che un comando e'
    /// partito senza gettone. Voce D166.
    /// </remarks>
    private List<MissionStep>? StepsOfGroup(Guid groupedMovementId, string origin)
    {
        if (groupedMovementId != Guid.Empty)
            return Collections.MissionSteps.GetGroupedMovementId(groupedMovementId);

        Navigator.WriteLog(
            $"{origin} {device.ItemGuid}: gettone di gruppo vuoto, nessuno step selezionato",
            LogEntryType.Warning);
        return null;
    }

    /// <summary>
    /// Costruisce una descrizione dell'errore dettagliata basata sui parametri dello step fallito,
    /// includendo informazioni sui dispositivi sorgente e destinazione.
    /// </summary>
    /// <param name="stepError">Lo step della missione che ha generato l'errore</param>
    /// <param name="fromAsiItem">L'elemento ASI sorgente</param>
    /// <param name="toAsiItem">L'elemento ASI destinazione</param>
    /// <param name="errorDescription">La descrizione base dell'errore</param>
    /// <returns>Una descrizione dettagliata dell'errore formattata</returns>
    private string BuildErrorDescription(MissionStep? stepError, dynamic fromAsiItem, dynamic toAsiItem, string errorDescription)
    {
        // Null checks per evitare NullReferenceException
        if (stepError?.PathMission == null)
            return errorDescription;

        // Verifica che fromAsiItem e toAsiItem non siano null
        if (fromAsiItem == null || toAsiItem == null)
            return errorDescription;

        var luCode = stepError.PathMission.LuCode ?? "Unknown";

        if (stepError.ToNodeId == device.ItemGuid)
        {
            // Verifica che SourceItem esista
            if (stepError.SourceItem == null)
                return errorDescription;

            switch (stepError.SourceItem.DeviceType)
            {
                case DeviceTypes.Storage:
                    return $"Error PICK UP LU {luCode} for mission from {fromAsiItem?.Asi}.{fromAsiItem?.Subitem}.{fromAsiItem?.Partition} to {toAsiItem?.Asi}.{toAsiItem?.Subitem}.{toAsiItem?.Partition} Error: {errorDescription}";

                default:
                    stepError.SourceItem.Status = DeviceStatusTypes.Ready;
                    if (stepError.SourceItem.MotionType == MotionTypes.GetFrom)
                        stepError.SourceItem.LUtoReceive = null;
                    return $"Error moving LU {luCode} for mission from {fromAsiItem?.Asi}.{fromAsiItem?.Subitem}.{fromAsiItem?.Partition} to {toAsiItem?.Asi}.{toAsiItem?.Subitem}.{toAsiItem?.Partition} Error: {errorDescription}";
            }
        }
        else if (stepError.FromNodeId == device.ItemGuid)
        {
            // Verifica che DestinationItem esista
            if (stepError.DestinationItem == null)
                return errorDescription;

            switch (stepError.DestinationItem.DeviceType)
            {
                case DeviceTypes.Storage:
                    return $"Error Storing LU {luCode} for mission from {fromAsiItem?.Asi} . {fromAsiItem?.Subitem} . {fromAsiItem?.Partition}  to  {toAsiItem?.Asi} . {toAsiItem?.Subitem} . {toAsiItem?.Partition} Error: {errorDescription}";

                default:
                    stepError.DestinationItem.Status = stepError.DestinationItem.MotionType == MotionTypes.GetFrom
                        ? DeviceStatusTypes.Warning
                        : DeviceStatusTypes.Ready;
                    return $"Error moving LU {luCode} for mission from {fromAsiItem?.Asi}.{fromAsiItem?.Subitem}.{fromAsiItem?.Partition} to {toAsiItem?.Asi}.{toAsiItem?.Subitem}.{toAsiItem?.Partition} Error: {errorDescription}";
            }
        }

        return errorDescription;
    }

    /// <summary>
    /// Costruisce una descrizione dell'errore per operazioni di gruppo, gestendo
    /// la logica di errore specifica per dispositivi raggruppati.
    /// </summary>
    /// <param name="luCode">Il codice della LU coinvolta nell'errore</param>
    /// <param name="sourceItem">Il dispositivo sorgente</param>
    /// <param name="destinationItem">Il dispositivo destinazione</param>
    /// <param name="fromAsiItem">L'elemento ASI sorgente</param>
    /// <param name="toAsiItem">L'elemento ASI destinazione</param>
    /// <param name="errorDescription">La descrizione base dell'errore</param>
    /// <param name="itemsToExecuteStep">Lista dei dispositivi che devono eseguire uno step</param>
    /// <returns>Una descrizione dettagliata dell'errore di gruppo formattata</returns>
    private string BuildGroupErrorDescription(string? luCode, AutomationDevice sourceItem, AutomationDevice destinationItem,
        dynamic fromAsiItem, dynamic toAsiItem, string errorDescription, List<AutomationDevice> itemsToExecuteStep)
    {
        if (destinationItem.BelongingGroupId == device.GroupItemId)
        {
            itemsToExecuteStep.Add(sourceItem);

            switch (sourceItem.DeviceType)
            {
                case DeviceTypes.Storage:
                    return $"Error Picking Up LU {luCode} to move from {fromAsiItem.Asi}.{fromAsiItem.Subitem}.{fromAsiItem.Partition} to {toAsiItem.Asi}.{toAsiItem.Subitem}.{toAsiItem.Partition} Error: {errorDescription}";

                default:
                    if (destinationItem.MotionType == MotionTypes.MoveTo)
                        destinationItem.LUtoReceive = null;
                    return $"Error Moving LU {luCode} to move from {fromAsiItem.Asi}.{fromAsiItem.Subitem}.{fromAsiItem.Partition} to {toAsiItem.Asi}.{toAsiItem.Subitem}.{toAsiItem.Partition} Error: {errorDescription}";
            }

        }
        else if (sourceItem.BelongingGroupId == device.GroupItemId)
        {
            itemsToExecuteStep.Add(sourceItem);

            switch (destinationItem.DeviceType)
            {
                case DeviceTypes.Storage:
                    return $"Error Storing LU {luCode} from {fromAsiItem.Asi}.{fromAsiItem.Subitem}.{fromAsiItem.Partition} to {toAsiItem.Asi}.{toAsiItem.Subitem}.{toAsiItem.Partition} Error: {errorDescription}";

                default:
                    if (destinationItem.MotionType == MotionTypes.MoveTo)
                        destinationItem.LUtoReceive = null;

                    return $"Error Moving LU {luCode} from {fromAsiItem.Asi}.{fromAsiItem.Subitem}.{fromAsiItem.Partition} to {toAsiItem.Asi}.{toAsiItem.Subitem}.{toAsiItem.Partition} Error: {errorDescription}";
            }
        }

        return errorDescription;
    }

    /// <summary>
    /// Costruisce una descrizione dell'errore di timeout per operazioni di gruppo,
    /// gestendo la logica specifica per timeout e errori di connessione nei dispositivi raggruppati.
    /// </summary>
    /// <param name="stepError">Lo step della missione che ha generato l'errore</param>
    /// <param name="fromAsiItem">L'elemento ASI sorgente</param>
    /// <param name="toAsiItem">L'elemento ASI destinazione</param>
    /// <param name="errorType">Il tipo di errore (timeout, connessione, ecc.)</param>
    /// <param name="itemsToExecuteStep">Lista dei dispositivi che devono eseguire uno step</param>
    /// <returns>Una descrizione dettagliata dell'errore di timeout di gruppo formattata</returns>
    private string BuildGroupTimeoutErrorDescription(MissionStep stepError, dynamic fromAsiItem, dynamic toAsiItem,
        string errorType, List<AutomationDevice> itemsToExecuteStep)
    {
        var luCode = stepError.PathMission?.LuCode;
        var sourceItem = stepError.SourceItem;
        var destinationItem = stepError.DestinationItem;

        if (destinationItem != null && destinationItem.BelongingGroupId == device.GroupItemId)
        {
            if (sourceItem == null) return string.Empty;

            itemsToExecuteStep.Add(sourceItem);

            switch (sourceItem.DeviceType)
            {
                case DeviceTypes.Storage:
                    return
                        $"PLC {errorType} Error Picking UP LU {luCode} for mission from {fromAsiItem.Asi}.{fromAsiItem.Subitem}.{fromAsiItem.Partition} to {toAsiItem.Asi}.{toAsiItem.Subitem}.{toAsiItem.Partition}";

                default:
                    if (destinationItem.MotionType == MotionTypes.MoveTo)
                        destinationItem.LUtoReceive = null;

                    return
                        $"PLC {errorType} Error Moving LU {luCode} for mission from {fromAsiItem.Asi}.{fromAsiItem.Subitem}.{fromAsiItem.Partition} to {toAsiItem.Asi}.{toAsiItem.Subitem}.{toAsiItem.Partition}";
            }
        }
        else if (sourceItem != null && sourceItem.BelongingGroupId == device.GroupItemId)
        {
            itemsToExecuteStep.Add(sourceItem);

            if (destinationItem == null) return string.Empty;

            switch (destinationItem.DeviceType)
            {
                case DeviceTypes.Storage:
                    return
                        $"PLC {errorType} Error Storing LU {luCode} for mission from {fromAsiItem.Asi}.{fromAsiItem.Subitem}.{fromAsiItem.Partition} to {toAsiItem.Asi}.{toAsiItem.Subitem}.{toAsiItem.Partition}";

                default:
                    if (destinationItem.MotionType == MotionTypes.MoveTo)
                        destinationItem.LUtoReceive = null;

                    return
                        $"PLC {errorType} Error Moving LU {luCode} for mission from {fromAsiItem.Asi}.{fromAsiItem.Subitem}.{fromAsiItem.Partition} to {toAsiItem.Asi}.{toAsiItem.Subitem}.{toAsiItem.Partition}";
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Costruisce una descrizione dell'errore di timeout per operazioni singole,
    /// gestendo la logica specifica per timeout e errori di connessione nei dispositivi individuali.
    /// </summary>
    /// <param name="stepError">Lo step della missione che ha generato l'errore</param>
    /// <param name="fromAsiItem">L'elemento ASI sorgente</param>
    /// <param name="toAsiItem">L'elemento ASI destinazione</param>
    /// <param name="errorType">Il tipo di errore (timeout, connessione, ecc.)</param>
    /// <returns>Una descrizione dettagliata dell'errore di timeout formattata</returns>
    private string BuildTimeoutErrorDescription(MissionStep stepError, dynamic fromAsiItem, dynamic toAsiItem, string errorType)
    {

        if (stepError.ToNodeId == device.ItemGuid)
        {
            if (stepError.SourceItem == null) return string.Empty;
            switch (stepError.SourceItem.DeviceType)
            {
                case DeviceTypes.Storage:
                    return
                        $"PLC {errorType} Error Picking UP LU {stepError.PathMission?.LuCode} for mission from {fromAsiItem.Asi}.{fromAsiItem.Subitem}.{fromAsiItem.Partition} to {toAsiItem.Asi}.{toAsiItem.Subitem}.{toAsiItem.Partition}";

                default:
                    stepError.SourceItem.Status = DeviceStatusTypes.Ready;
                    if (stepError is { DestinationItem: not null, PathMission: not null })
                        UpdateGroupExecutionStatus(stepError.DestinationItem, stepError.PathMission);

                    if (stepError.SourceItem.MotionType == MotionTypes.GetFrom)
                        stepError.SourceItem.LUtoReceive = null;

                    return
                        $"PLC {errorType} Error Moving LU {stepError.PathMission?.LuCode} from {fromAsiItem.Asi}.{fromAsiItem.Subitem}.{fromAsiItem.Partition} to {toAsiItem.Asi}.{toAsiItem.Subitem}.{toAsiItem.Partition}";
            }
        }
        else if (stepError.FromNodeId == device.ItemGuid)
        {
            if (stepError.DestinationItem == null) return string.Empty;
            switch (stepError.DestinationItem.DeviceType)
            {
                case DeviceTypes.Storage:
                    return $"PLC {errorType} Error Storing LU {stepError.PathMission?.LuCode} for mission from {fromAsiItem.Asi}.{fromAsiItem.Subitem}.{fromAsiItem.Partition} to {toAsiItem.Asi}.{toAsiItem.Subitem}.{toAsiItem.Partition}";

                default:
                    stepError.DestinationItem.Status = stepError.DestinationItem.MotionType == MotionTypes.GetFrom
                        ? DeviceStatusTypes.Warning
                        : DeviceStatusTypes.Ready;

                    return $"PLC {errorType} Error Moving LU {stepError.PathMission?.LuCode} from {fromAsiItem.Asi}.{fromAsiItem.Subitem}.{fromAsiItem.Partition} to {toAsiItem.Asi}.{toAsiItem.Subitem}.{toAsiItem.Partition}";
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Calcola la profondità basata sulla partizione della posizione finale,
    /// considerando il tipo di registrazione della lunghezza di storage del dispositivo.
    /// </summary>
    /// <param name="finalPosPartition">La partizione della posizione finale</param>
    /// <returns>La profondità calcolata, -1 se non valida</returns>
    private int CalculateDepth(string? finalPosPartition)
    {
        var depth = -1;
        if (string.IsNullOrEmpty(finalPosPartition)) return depth;

        if (device.RecordStorageLength != RecordStorageTypes.FromPlc) return depth;
        if (device.ConnectionType != ConnectionTypes.Test &&
            int.TryParse(finalPosPartition, out depth))
            depth *= 10;
        else
            depth = -1;

        return depth;
    }

    /// <summary>
    /// Gestisce errori di default che non rientrano in categorie specifiche,
    /// con particolare attenzione ai processi fisici di svuotamento cella.
    /// </summary>
    /// <param name="e">Gli argomenti dell'evento di movimento completato con errore</param>
    private async Task HandleDefaultError(AsiMovementIsCompletedEventArgs e)
    {
        var executedStep = Collections.MissionSteps.Instance.FirstOrDefault(x => x.StepId == e.StepGuid);

        if (executedStep == null)
        {
            await StepError(e.StepGuid, e.ItemGuid, e.ErrorCode, e.ErrorDescription);
            return;
        }

        if (IsEmptyCellPhysicalProcess(e, executedStep))
        {
            await HandleEmptyCellPhysicalProcess(e, executedStep);
        }
        else
        {
            await StepError(e.StepGuid, e.ItemGuid, e.ErrorCode, e.ErrorDescription);
        }
    }

    /// <summary>
    /// Gestisce il processo fisico di svuotamento cella quando si verifica un errore,
    /// aggiornando lo stato delle missioni e pulendo le proprietà della cella vuota.
    /// </summary>
    /// <param name="e">Gli argomenti dell'evento di movimento completato con errore</param>
    /// <param name="executedStep">Lo step della missione che stava venendo eseguito</param>
    private async Task HandleEmptyCellPhysicalProcess(AsiMovementIsCompletedEventArgs e, MissionStep executedStep)
    {
        var errorDescription = $"The physical cell emptying process {device.EmptyCellSourceDoor} is finished";

        if (executedStep.PathMission?.IdCommandToDo > 0)
        {
            await CommandToDo.SetStatus(executedStep.PathMission.IdCommandToDo, Command.StatusTypes.Error, errorDescription);
        }
        else
        {
            if (executedStep.PathMission != null)
            {
                executedStep.PathMission.MovingDescription = errorDescription;
                executedStep.PathMission.Status = MissionStatusTypes.Error;
            }

            Navigator.RecordNavigatorLog(device, errorDescription, LogEntryType.Error, executedStep);

            await CommandToDo.Update(new Command
            {
                CommandType = Command.CommandTypes.ItemExecuteStep,
                Date = DateTime.Now,
                ErrorDescription = errorDescription,
                ItemCommandId = e.ItemGuid,
                LuId = 0,
                Status = Command.StatusTypes.Error,
                WorkstationId = Navigator.WorkstationId
            });
        }

        // Reset empty cell properties
        device.EmptyCellIdWorkstation = string.Empty;
        device.EmptyCellIdLuType = 0;
        device.EmptyCellSourceDoor = string.Empty;
        device.EmptyCellDestinationDoor = string.Empty;

        if (executedStep.PathMission != null)
        {
            if (executedStep.PathMission.LuId > 0)
                Navigator.WriteLog(
                    $"DEBUG INFO: Cancellazione MISSION_LU da ManageMoveToCompleteError 1 UDC: {executedStep.PathMission.LuId} - {executedStep.PathMission.LuCode} Date: {DateTime.Now:dd/MM/yyyy HH:mm:ss}",
                    LogEntryType.Information);

            await Collections.ActiveMissions.Instance.RemoveUnused(executedStep.PathMission);
        }

        if (device.LUtoPick?.LuId > 0)
            Navigator.WriteLog(
                $"DEBUG INFO: Cancellazione ESTRAZIONI_UDC_AUTOMATICHE da ManageMoveToCompleteError 2 UDC: {device.LUtoPick.LuId} - {device.LUtoPick.LuCode} Date: {DateTime.Now:dd/MM/yyyy HH:mm:ss}",
                LogEntryType.Information);

        await Collections.ActiveMissions.Instance.RemoveUnused(device.LUtoPick);

        device.EmptyCellStatus = EmptyCellStatusTypes.None;
        device.RunningStepId = Guid.Empty;
        device.LUtoPick = null;
        device.Status = DeviceStatusTypes.Ready;
    }

    /// <summary>
    /// Gli errori 67, 85 e 133 sul deposito del ricollocatore di gruppo: le missioni degli step del
    /// movimento rifiutato cercano una cella nuova, esclusa quella rifiutata, e senza un'altra cella
    /// vanno allo scarto (risposta 1 del proprietario dell'8/10).
    /// </summary>
    /// <param name="e">Gli argomenti dell'evento di movimento completato con errore</param>
    /// <remarks>
    /// <para>
    /// Le regole sono quelle del dispositivo singolo (<see cref="HandleStorageErrors"/>): il conto per
    /// codice sugli errori di deposito di fila (<c>AutomationDevice.CountRefusedDeposit</c>), al quarto
    /// errore uguale il gruppo va in errore e aspetta, senza cercare; con il gestionale che non risponde,
    /// o senza un'altra cella e senza un punto di scarto, il gruppo va in errore e aspetta. Il tetto di
    /// due 67 di fila del compito 5 non c'e' piu', e nemmeno lo scarto di ogni missione a bordo: la
    /// ricerca e' quella del gruppo (<c>AutomationDeviceStorageManager.FindNewStoreLocationGrouped</c>),
    /// e cambia cella solo la missione il cui deposito il PLC ha rifiutato. Prima gli errori 85 e 133
    /// del gruppo andavano subito in errore, e il 67 mandava allo scarto ogni missione della
    /// configurazione.
    /// </para>
    /// <para>
    /// Il 67 («cella piena») blocca la cella nel gestionale, come prima, ma solo quella delle missioni
    /// rifiutate: un'altra missione a bordo tiene la sua, e bloccarla la rifiuterebbe al suo deposito.
    /// Gli step del movimento si prendono prima della ricerca, che li toglie: con l'errore li marca
    /// <c>StepErrorGroup</c>, e la rotta, se la ricerca l'ha tolta, la rifa' il navigatore quando
    /// l'operatore rimette in marcia il gruppo. Nessuna chiamata al gestionale dentro un lucchetto o un
    /// semaforo: questo gestore gira sul completamento del movimento.
    /// </para>
    /// <para>
    /// Se il gestionale non risponde (al blocco del 67 o alla ricerca), dopo l'errore le missioni
    /// rifiutate lasciano la porta rifiutata, come una cella persa, e il navigatore cerca loro un'altra
    /// cella, con le celle rifiutate ancora escluse, quando il gestionale risponde
    /// (<see cref="MissionGateway.LeaveTheRefusedCell"/>, risposta 7 del proprietario dell'8/10 sera).
    /// Le altre missioni a bordo non si toccano (risposta 8).
    /// </para>
    /// </remarks>
    private async Task HandleGroupStorageErrors(AsiMovementIsCompletedEventArgs e)
    {
        // Il gruppo che deframmenta, o senza UDC a bordo, va in errore senza contare: la fila degli errori
        // di deposito resta com'e', perche' la azzerano solo un movimento riuscito e l'operatore (risposta
        // 5 del proprietario dell'8/10 sera). Prima qui si azzerava, come faceva AttemptsToChangeLocation.
        if (device is not { IdCellToDefragment: 0, LUsLoaded.Count: > 0 })
        {
            await StepErrorGroup(e.StepGuid, e.ItemGuid, e.ErrorCode, e.ErrorDescription);
            return;
        }

        var refusedSteps = StepsOfGroup(e.StepGuid, nameof(HandleGroupStorageErrors));
        if (refusedSteps is not { Count: > 0 })
        {
            await StepErrorGroup(e.StepGuid, e.ItemGuid, e.ErrorCode, e.ErrorDescription);
            return;
        }

        var refused = refusedSteps.Select(step => step.PathMission).OfType<PathMission>().Distinct().ToList();
        var worker = $"{nameof(HandleGroupStorageErrors)} {device.ItemGuid}";

        // Gli step del movimento, presi prima della ricerca che li toglie: l'errore li marca anche dopo.
        if (!await AnotherCellAfterTheRefusalAsync(e, refused, worker,
                () => StepErrorGroup(refusedSteps, e.ItemGuid, e.ErrorCode, e.ErrorDescription)))
            return;

        foreach (var stepError in refusedSteps)
        {
            // Anche il posto, non solo lo stato. Voce D73.
            if (stepError.DestinationItem != null)
                stepError.DestinationItem.ReleaseAfterFailedStep(stepError.StepId);
            if (stepError.SourceItem != null)
                stepError.SourceItem.Status = DeviceStatusTypes.Ready;
        }

        // Attesa fissa tolta con D67: compensava l'asincronia di Status, sincrona da D43.
        device.RunningStepId = Guid.Empty;
        device.Status = DeviceStatusTypes.Ready;
        device.GroupExecuteStepRunning = false;
    }

    /// <summary>
    /// La regola degli errori 67, 85 e 133, la stessa per il gruppo (<see cref="HandleGroupStorageErrors"/>)
    /// e per il dispositivo singolo (<see cref="HandleStorageErrors"/>): il blocco della cella piena del 67,
    /// il conto dei rifiuti uguali di fila, la ricerca di una cella nuova, e con il gestionale muto le
    /// missioni rifiutate che lasciano la porta rifiutata. Prima la regola era scritta due volte, e le due
    /// copie marcavano l'errore ognuna a modo suo, ramo per ramo (revisione finale del terzo giro).
    /// </summary>
    /// <param name="refused">Le missioni il cui deposito il PLC ha rifiutato.</param>
    /// <param name="worker">Chi chiede, per le righe di registro.</param>
    /// <param name="markTheError">
    /// L'errore del dispositivo, con gli step del deposito rifiutato presi prima della ricerca, che li puo'
    /// togliere: il dispositivo va in errore e aspetta l'operatore.
    /// </param>
    /// <returns>
    /// Vero se le missioni rifiutate hanno una cella nuova, o sono andate allo scarto: chi chiama fa
    /// ripartire il dispositivo. Falso se il dispositivo e' gia' in errore; con il gestionale muto le
    /// missioni hanno anche gia' lasciato la porta rifiutata.
    /// </returns>
    private async Task<bool> AnotherCellAfterTheRefusalAsync(AsiMovementIsCompletedEventArgs e,
        IReadOnlyCollection<PathMission> refused, string worker, Func<Task> markTheError)
    {
        // Le porte rifiutate, prima della ricerca che puo' cambiarle: con il gestionale muto si lasciano
        // solo queste.
        var refusedDoors = refused.Select(mission => (Mission: mission, Door: mission.StorageDoorCode)).ToList();

        // Il 67 e' «cella piena»: la cella si blocca nel gestionale a ogni 67, anche al quarto, che non
        // cerca piu' ma la cella resta piena. Le celle che il gestionale muto non ha bloccato vanno
        // nell'elenco da ritentare al giro del freno (risposta 3 del proprietario del 9/10).
        var locked = e.ErrorCode == 67 ? await LockTheRefusedCellsAsync(refused, worker) : CellLockResults.Locked;

        // Il conto si guarda prima dell'esito del blocco: al quarto rifiuto uguale il dispositivo va in
        // errore e aspetta (risposta 1 del proprietario dell'8/10), senza ricerca. Con il gestionale muto
        // al blocco del 67, in piu', le missioni rifiutate lasciano la porta come una cella persa, come
        // prima del quarto (risposta 1 del 9/10): alla ripartenza il navigatore cerca un'altra cella, con
        // le rifiutate escluse. Prima, al quarto, tenevano la porta rifiutata, che il gestionale non sapeva
        // piena e poteva dare a un'altra UDC.
        //
        // Il gestionale non ha risposto al blocco, o il blocco e' caduto: adesso non si sa, e il
        // dispositivo va in errore e aspetta, senza ricerca, come quando il gestionale non risponde alla
        // ricerca (risposta 1 del proprietario dell'8/10). Il PLC il deposito l'ha rifiutato comunque, e
        // il conto lo conta. Prima l'eccezione usciva dal gestore e la prendeva solo il log del
        // completamento: il dispositivo restava impegnato in silenzio, con lo step in volo, senza errore
        // e senza allarme. Le due uscite hanno lo stesso corpo, e si scrive una volta.
        var searchAgain = await CountTheRefusalAsync(e, refused, worker);

        if (!searchAgain || locked != CellLockResults.Locked)
        {
            await markTheError();
            if (locked == CellLockResults.WarehouseSilent) LeaveTheRefusedCells(refusedDoors, worker);
            return false;
        }

        // StorageManager e' costruito da Initialize e non torna mai nullo: la guardia sostituisce
        // "await x?.M()", che sollevava NullReferenceException.
        var outcome = device.StorageManager == null
            ? AutomationDeviceStorageManager.RefusedDepositOutcomes.NoPlace
            : await device.StorageManager.AnotherCellAfterARefusedDepositAsync(refused, worker);

        if (outcome is AutomationDeviceStorageManager.RefusedDepositOutcomes.Found or
            AutomationDeviceStorageManager.RefusedDepositOutcomes.TakenOver or
            AutomationDeviceStorageManager.RefusedDepositOutcomes.Rejected)
            return true;

        await markTheError();

        // Dopo l'errore, che mette il dispositivo in Warning: lo step rifiutato non e' piu' in volo.
        if (outcome == AutomationDeviceStorageManager.RefusedDepositOutcomes.WarehouseSilent)
            LeaveTheRefusedCells(refusedDoors, worker);

        return false;
    }

    /// <summary>
    /// Dopo un deposito rifiutato e il gestionale muto, con il dispositivo gia' in errore: le missioni
    /// rifiutate lasciano la porta rifiutata (<see cref="MissionGateway.LeaveTheRefusedCell"/>, risposta
    /// 7 del proprietario dell'8/10 sera). Solo quelle la cui porta e' ancora quella rifiutata. La missione
    /// che presta la porta la fa lasciare alle sue copie, e una copia la cui missione, rifiutata con lei,
    /// tiene ancora la porta resta con lei, in qualunque ordine vengano; se la missione non e' fra le
    /// rifiutate, la copia lascia la porta come una cella persa sua.
    /// </summary>
    /// <remarks>
    /// L'ordine e' fisso: prima le copie (<see cref="MissionGateway.IsACopy"/>), poi le altre, nell'ordine
    /// in cui vengono. Le missioni di un movimento di gruppo arrivano nell'ordine del dizionario degli step,
    /// con chiavi casuali: l'esito non cambia con l'ordine, ma un ordine casuale faceva passare ogni volta
    /// una sola delle due strade. Con le copie prima, una copia rifiutata con la sua missione passa sempre
    /// dalla lista <c>refused</c> (resta con lei, che poi la fa lasciare con le sue copie); l'altro ordine
    /// lo prova la prova diretta di <see cref="MissionGateway.LeaveTheRefusedCell"/> (revisione finale del
    /// terzo giro).
    /// </remarks>
    private static void LeaveTheRefusedCells(IReadOnlyCollection<(PathMission Mission, string? Door)> refusedDoors,
        string worker)
    {
        var refused = refusedDoors.Select(refusedDoor => refusedDoor.Mission).ToList();

        foreach (var (mission, door) in refusedDoors.OrderBy(refusedDoor => MissionGateway.IsACopy(refusedDoor.Mission) ? 0 : 1))
            MissionGateway.LeaveTheRefusedCell(mission, door, worker, refused);
    }

    /// <summary>L'esito di <see cref="LockTheRefusedCellsAsync"/>.</summary>
    private enum CellLockResults
    {
        /// <summary>
        /// Il gestionale ha risposto per ogni cella rifiutata: l'ha bloccata come piena, o ha rifiutato di
        /// farlo con un 4xx. Tutte e due sono una risposta, e il 67 prosegue.
        /// </summary>
        Locked,

        /// <summary>Il gestionale non ha risposto: il dispositivo va in errore, e le missioni lasciano la porta.</summary>
        WarehouseSilent,

        /// <summary>Il blocco e' caduto per un altro guasto: il dispositivo va in errore e aspetta.</summary>
        Failed
    }

    /// <summary>
    /// Blocca come piene nel gestionale le celle delle missioni il cui deposito il PLC ha rifiutato con
    /// il 67, una volta per porta.
    /// </summary>
    /// <returns>
    /// Se il gestionale non ha risposto, o il blocco e' caduto, il chiamante va in errore; con il
    /// gestionale muto, in piu', le missioni rifiutate lasciano la porta.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Con il gestionale muto le celle non bloccate (quella della domanda caduta e quelle dopo di lei)
    /// vanno nell'elenco delle celle da segnare piene (<see cref="CellsToMarkFull"/>), che il giro del
    /// freno del navigatore ritenta finche' il gestionale risponde (risposta 3 del proprietario del
    /// 9/10). La riga di avviso qui sotto e' la sola: i ritentativi che non riescono non scrivono.
    /// </para>
    /// <para>
    /// Un 4xx del gestionale (<see cref="WarehouseUnavailableException.IsPermanentRejection"/>) e' una
    /// risposta, anche al primo blocco: una riga di avviso per la cella, niente elenco, e si passa alla
    /// porta dopo; il 67 prosegue come dopo un blocco riuscito (risposta 2 del proprietario del 9/10
    /// pomeriggio). Prima il 4xx valeva il gestionale muto: il dispositivo andava in errore, la missione
    /// lasciava la porta e la cella entrava nell'elenco, da cui il primo ritentativo la toglieva con la
    /// riga del rifiuto.
    /// </para>
    /// </remarks>
    private async Task<CellLockResults> LockTheRefusedCellsAsync(IReadOnlyCollection<PathMission> refused, string worker)
    {
        var doors = refused.Select(lu => lu.StorageDoorCode).OfType<string>()
            .Where(door => door.Length > 0).Distinct().ToList();
        // Le porte a cui il gestionale ha risposto: bloccate, o rifiutate con un 4xx.
        var answered = new List<string>();

        try
        {
            foreach (var lu in refused)
            {
                if (lu.StorageDoorCode is not { Length: > 0 } door || answered.Contains(door)) continue;

                try
                {
                    // Compito 9: ex DbInfo.SetLockCell(doorCode, CellLockTypes.Full), stessa costante
                    // passata sempre (nessun chiamante ne passava un'altra) - ora CellLockState.Full,
                    // l'enum del contratto con lo stesso valore.
                    await lu.SetCellLockStateAsync(door, CellLockState.Full);
                }
                catch (WarehouseUnavailableException rejection) when (rejection.IsPermanentRejection)
                {
                    // Il gestionale ha risposto di no: riprovare non lo cambia, e la cella non va
                    // nell'elenco. Il PLC l'ha rifiutata comunque: la missione la esclude lo stesso.
                    Navigator.WriteLog(
                        $"{worker}: il gestionale ha rifiutato di segnare piena la cella della porta {door} dopo l'errore 67 (risposta {(int)rejection.StatusCode!.Value}): non si ritenta, e il 67 prosegue come dopo un segno riuscito",
                        LogEntryType.Warning);
                }

                answered.Add(door);
            }

            return CellLockResults.Locked;
        }
        catch (Exception failure)
        {
            var silent = failure is WarehouseUnavailableException;
            var notLocked = doors.Except(answered).ToList();

            if (!silent)
            {
                Navigator.WriteLog(
                    $"{worker}: il blocco della cella piena dopo l'errore 67 e' caduto ({failure.GetType().Name}: {failure.Message}): il dispositivo va in errore e aspetta l'operatore",
                    LogEntryType.Warning);
                return CellLockResults.Failed;
            }

            // La riga dice che il blocco si ritenta solo dopo che le porte sono nell'elenco: prima la
            // scriveva prima, e se la scrittura cadeva una seconda riga diceva il contrario.
            var notRemembered = await RememberTheCellsToMarkFullAsync(notLocked);

            Navigator.WriteLog(notRemembered == null
                    ? $"{worker}: il gestionale non risponde al blocco della cella piena dopo l'errore 67 ({failure.Message}): il dispositivo va in errore e aspetta l'operatore, e il blocco delle porte {string.Join(", ", notLocked)} si ritenta finche' il gestionale risponde"
                    : $"{worker}: il gestionale non risponde al blocco della cella piena dopo l'errore 67 ({failure.Message}): il dispositivo va in errore e aspetta l'operatore, e il blocco delle porte {string.Join(", ", notLocked)} non si ritenta: non sono entrate nell'elenco delle celle da segnare piene ({notRemembered.Message})",
                LogEntryType.Warning);

            return CellLockResults.WarehouseSilent;
        }
        finally
        {
            // Le porte a cui il gestionale ha risposto escono dall'elenco delle celle da segnare piene,
            // se c'erano da un blocco di prima con il gestionale muto (CellsToMarkFull.AnsweredAsync).
            await CellsToMarkFull.AnsweredAsync(answered);
        }
    }

    /// <summary>
    /// Le celle che il gestionale muto non ha bloccato dopo il 67, nell'elenco da ritentare
    /// (<see cref="CellsToMarkFull"/>): una scrittura del database del WCS, niente gestionale. Se la
    /// scrittura cade il dispositivo va in errore lo stesso, e la riga di avviso del blocco lo dice.
    /// </summary>
    /// <returns>Il guasto della scrittura; <c>null</c> se le porte sono nell'elenco.</returns>
    private static async Task<Exception?> RememberTheCellsToMarkFullAsync(IReadOnlyCollection<string> doors)
    {
        try
        {
            await CellsToMarkFull.AddAsync(doors);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>
    /// Conta il deposito rifiutato dal PLC sul dispositivo (<c>AutomationDevice.CountRefusedDeposit</c>),
    /// aggiunge le celle rifiutate a quelle che ogni missione rifiutata esclude
    /// (<see cref="PathMission.CellsRefusedByThePlc"/>, anche al quarto errore, senza ricerca), e dice se
    /// si cerca ancora una cella nuova.
    /// </summary>
    /// <returns>
    /// Falso al quarto errore uguale di fila: il dispositivo va in errore e aspetta, senza ricerca e
    /// senza scarto (risposta 1 del proprietario dell'8/10).
    /// </returns>
    /// <remarks>
    /// Le celle di tutto il movimento rifiutato valgono per ogni missione del movimento, come quando
    /// erano del dispositivo: la fila di una cella piena e' piena per tutti.
    /// </remarks>
    private async Task<bool> CountTheRefusalAsync(AsiMovementIsCompletedEventArgs e,
        IReadOnlyCollection<PathMission> refused, string worker)
    {
        // Letture del database del WCS soltanto, niente gestionale.
        var cells = new List<decimal>();
        foreach (var mission in refused)
            cells.Add(await DbInfo.GetCellId(mission.StorageDoorCode));

        foreach (var mission in refused)
            mission.RememberCellsRefusedByThePlc(cells);

        var inARow = device.CountRefusedDeposit(e.ErrorCode);
        if (inARow <= AutomationDevice.SearchesForTheSameRefusal) return true;

        Navigator.WriteLog(
            $"{worker}: il PLC ha rifiutato il deposito con l'errore {e.ErrorCode} per la {inARow}a volta di fila: nessuna cella nuova, il dispositivo va in errore e aspetta l'operatore",
            LogEntryType.Warning);
        return false;
    }

    /// <summary>
    /// Gestisce gli errori di movimento routing la gestione verso i metodi appropriati
    /// basati sul tipo di dispositivo (singolo o gruppo).
    /// </summary>
    /// <param name="e">Gli argomenti dell'evento di movimento completato con errore</param>
    private async Task HandleMovementError(AsiMovementIsCompletedEventArgs e)
    {
        var isGroupDevice = device.DeviceType == DeviceTypes.GroupOfAutomationDevices || device.GroupItemId != 0;

        if (isGroupDevice)
            await ManageGroupedMoveToCompleteError(e);
        else
            await ManageMoveToCompleteError(e);
    }

    /// <summary>
    /// Gestisce i movimenti completati con successo, resettando i contatori di errore
    /// e processando i risultati basati sul tipo di dispositivo.
    /// </summary>
    /// <param name="e">Gli argomenti dell'evento di movimento completato con successo</param>
    private async Task HandleMovementSuccess(AsiMovementIsCompletedEventArgs e)
    {
        // Un deposito, o il prelievo di un'UDC nuova: la fila degli errori di deposito ricomincia
        // (risposta 5 del proprietario dell'8/10 sera). Si azzera a ogni movimento riuscito, senza
        // guardarne il tipo: un movimento del dispositivo prende o posa un'UDC, in un magazzino o su un
        // altro dispositivo, e posarla altrove (lo scarto, un altro dispositivo) e' un deposito riuscito.
        device.ForgetRefusedDeposits();

        var isGroupDevice = device.DeviceType == DeviceTypes.GroupOfAutomationDevices || device.GroupItemId != 0;

        if (e.MoveLuResult == null)
        {
            if (isGroupDevice)
                await device.StepDoneGroup(e.StepGuid, StepTypes.Automatic);
            else
                await device.StepDone(e.StepGuid, StepTypes.Automatic);
        }
        else
        {
            if (isGroupDevice)
            {
                var detectedDepths = e.MoveLuResult.Lu.Select(lu => new LuDetectedDepth
                {
                    LuId = lu.Id,
                    DetectedDepth = CalculateDepth(lu.FinalPosPartition)
                }).ToList();

                await device.StepDoneGroup(e.StepGuid, StepTypes.Automatic, detectedDepths);
            }
            else
            {
                var depth = CalculateDepth(e.MoveLuResult.Lu[0].FinalPosPartition);
                await device.StepDone(e.StepGuid, StepTypes.Automatic, depth);
            }
        }
    }

    /// <summary>
    /// Gestisce i timeout e gli errori di connessione durante i movimenti,
    /// routing la gestione verso i metodi appropriati basati sul tipo di dispositivo.
    /// </summary>
    /// <param name="e">Gli argomenti dell'evento di movimento completato con timeout o errore di connessione</param>
    private async Task HandleMovementTimeoutOrConnectionError(AsiMovementIsCompletedEventArgs e)
    {
        // La fila degli errori di deposito resta: un timeout non e' un deposito riuscito (risposta 5 del
        // proprietario dell'8/10 sera; 85, 85, 85, timeout, 85 e' il quarto 85 di fila). Prima la
        // dimenticava.
        var isGroupDevice = device.DeviceType == DeviceTypes.GroupOfAutomationDevices || device.GroupItemId != 0;

        if (isGroupDevice)
            await StepTimeOutGroup(e.StepGuid, e.ItemGuid, e.Result);
        else
            await StepTimeOut(e.StepGuid, e.ItemGuid, e.Result);
    }

    /// <summary>
    /// Gestisce il successo dell'invio di dati LU, processando la conferma dati
    /// e gestendo la logica specifica per tipo di dispositivo.
    /// </summary>
    /// <param name="e">Gli argomenti dell'evento di invio dati LU completato con successo</param>
    private async Task HandleSendLuDataSuccess(AsiSendLuDataEventArgs e)
    {
        if (e.LuDataFromAsiResult?.DataConfirm == 0)
        {
            if (device.LoadedLu != null)
            {
                device.LoadedLu.LuCode = string.Empty;
                device.LoadedLu.LuId = 0;
                device.LoadedLu.Status = MissionStatusTypes.Error;
                device.LoadedLu.StatusDescription = "Error on LU data from ASI";
                await device.LoadedLu.RejectTo(RejectionTypes.NoLuDataFromPlc);
            }

            if (device.AutoExecuteStep)
                _ = device.ExecuteStep();
            return;
        }

        var toBeRejected = await ProcessLuData(e);

        if (toBeRejected)
        {
            // Senza UDC caricata non c'e' nulla da respingere: ramo nullo silenzioso.
            if (device.LoadedLu != null) await device.LoadedLu.RejectTo(RejectionTypes.NoLuDataFromPlc);
            if (device.AutoExecuteStep) _ = device.ExecuteStep();
            return;
        }

        if (device.LoadedLu != null) device.LoadedLu.StatusDescription = string.Empty;

        ProcessDeviceTypeSpecificLogic();
    }

    /// <summary>
    /// Gestisce i timeout e gli errori di connessione durante l'invio di dati LU,
    /// impostando stati di errore appropriati e gestendo il rifiuto delle LU.
    /// </summary>
    /// <param name="e">Gli argomenti dell'evento di invio dati LU con timeout o errore di connessione</param>
    private async Task HandleSendLuDataTimeoutOrConnectionError(AsiSendLuDataEventArgs e)
    {
        try
        {
            if (device.LoadedLu == null)
            {
                if (device.AutoExecuteStep)
                    _ = device.ExecuteStep();
                return;
            }

            var errorType = e.Result switch
            {
                ResultCodeTypes.TimeOut => "Time Out",
                ResultCodeTypes.ConnectionError => "Connection",
                _ => "Unknown"
            };

            device.LoadedLu.Status = MissionStatusTypes.Error;
            device.LoadedLu.StatusDescription = $"PLC {errorType} Error - No data from PLC in expected time";

            Navigator.WriteAlert(device.LoadedLu);

            if (device.RejectItemDestinationIdGetLuInfo != Guid.Empty)
                await device.LoadedLu.RejectTo(RejectionTypes.NoLuDataFromPlc);
        }
        catch (Exception ex)
        {
            device.LogError(ex);
        }
    }

    /// <summary>
    /// Gli errori 67, 85 e 133 sul deposito di un dispositivo singolo, anche membro di un gruppo: una
    /// cella nuova per la missione del dispositivo, esclusa quella rifiutata, e senza un'altra cella lo
    /// scarto (risposta 1 del proprietario dell'8/10, per ogni missione).
    /// </summary>
    /// <param name="e">Gli argomenti dell'evento di movimento completato con errore</param>
    /// <returns>True se è necessario eseguire uno step, false altrimenti</returns>
    /// <remarks>
    /// <para>
    /// Il conto e' per codice, sugli errori di deposito di fila (<c>AutomationDevice.CountRefusedDeposit</c>):
    /// lo stesso codice per tre volte di fila cerca ogni volta una cella nuova, al quarto il dispositivo
    /// va in errore e aspetta l'operatore, senza ricerca e senza scarto. Un codice diverso fa ripartire
    /// il conto da uno; un movimento riuscito e l'operatore che rimette in marcia il dispositivo lo
    /// azzerano, un timeout no (risposta 5 del proprietario dell'8/10 sera). Prima i cambi di cella erano
    /// due, di qualunque codice, poi lo scarto.
    /// </para>
    /// <para>
    /// Il 67 («cella piena») blocca la cella nel gestionale anche qui, come sul gruppo (risposta 9 del
    /// proprietario dell'8/10 sera): prima la bloccava solo il ricollocatore di gruppo. Se il gestionale
    /// non risponde (al blocco o alla ricerca), dopo l'errore la missione lascia la porta rifiutata
    /// (<see cref="MissionGateway.LeaveTheRefusedCell"/>, risposta 7).
    /// </para>
    /// <para>
    /// La ricerca (<c>AutomationDeviceStorageManager.AnotherCellAfterARefusedDepositAsync</c>) e' quella
    /// del dispositivo di oggi: il flusso del cambio nel magazzino scelto per la missione dell'operatore,
    /// la ricerca del gruppo per un membro, la riverifica del ricollocatore per un dispositivo fuori da
    /// ogni gruppo. Con una cella nuova, o allo scarto, il dispositivo riparte; se il gestionale non
    /// risponde (o adesso non si sa), o senza un'altra cella e senza un punto di scarto, va in errore e
    /// aspetta: nessun nuovo deposito nella cella rifiutata, nessuno scarto. Lo step del deposito
    /// rifiutato si prende prima della ricerca, che puo' toglierlo: l'errore lo marca anche se non c'e'
    /// piu', e prima <c>StepError</c>, cercandolo per identificativo, dopo la ricerca del gruppo non lo
    /// trovava e non faceva niente.
    /// </para>
    /// </remarks>
    private async Task<bool> HandleStorageErrors(AsiMovementIsCompletedEventArgs e)
    {
        if (!device.IsLinkedToStorage || device.LoadedLu is not { } refused)
        {
            await StepError(e.StepGuid, e.ItemGuid, e.ErrorCode, e.ErrorDescription);
            return false;
        }

        // Lo step del deposito rifiutato, preso prima della ricerca che puo' toglierlo: l'errore lo marca
        // anche se non c'e' piu'. Senza uno step con quel gettone non c'e' niente da marcare (StepError per
        // identificativo lo cercherebbe e uscirebbe).
        var refusedStep = Collections.MissionSteps.Instance.FirstOrDefault(x => x.StepId == e.StepGuid);
        var worker = $"{nameof(HandleStorageErrors)} {device.ItemGuid}";

        if (!await AnotherCellAfterTheRefusalAsync(e, [refused], worker,
                () => refusedStep == null
                    ? Task.CompletedTask
                    : StepError(refusedStep, e.ItemGuid, e.ErrorCode, e.ErrorDescription)))
            return false;

        // Task.Delay e non Thread.Sleep: qui gira un thread del pool, condiviso da
        // tutti i dispositivi. Fermarlo ferma il lavoro di chi non c'entra.
        await Task.Delay(50);

        // Uno step concluso male non e' uno step in volo: il campo ha risposto. Va chiuso
        // qui, o il dispositivo resta «impegnato» e la riesecuzione subito sotto viene
        // rifiutata dalla guardia di ExecuteStep. Azzerarlo libera anche il posto che il
        // mittente aveva preso sulla destinazione (voce D73).
        device.RunningStepId = Guid.Empty;

        return device.AutoExecuteStep;
    }

    /// <summary>
    /// Verifica se l'errore corrente è relativo a un processo fisico di svuotamento cella.
    /// </summary>
    /// <param name="e">Gli argomenti dell'evento di movimento completato con errore</param>
    /// <param name="executedStep">Lo step della missione che stava venendo eseguito</param>
    /// <returns>True se è un processo di svuotamento cella fisico, false altrimenti</returns>
    private bool IsEmptyCellPhysicalProcess(AsiMovementIsCompletedEventArgs e, MissionStep executedStep)
    {
        return device.IsLinkedToStorage &&
               (device.EmptyCellStatus == EmptyCellStatusTypes.Active ||
                executedStep.PathMission?.LuId == 0) &&
               e.ErrorCode is 70 or 73;
    }

    /// <summary>
    /// Gestisce errori di movimento per dispositivi raggruppati, routing verso
    /// gestori specifici basati sul codice di errore.
    /// </summary>
    /// <param name="e">Gli argomenti dell'evento di movimento completato con errore</param>
    private async Task ManageGroupedMoveToCompleteError(AsiMovementIsCompletedEventArgs? e)
    {
        if (e == null) return;

        switch (e.ErrorCode)
        {
            // Il 137 («area in errore») non ha un ramo suo: nessun tentativo, il gruppo va subito in
            // errore e aspetta l'operatore, al deposito e al prelievo, come ogni altro errore (risposta
            // 1 del proprietario dell'8/10). Prima ritentava fino a dieci volte, contate in
            // Retries89Error, sparito con i tentativi; poi un gestore suo che inoltrava soltanto. Con il
            // gettone vuoto StepErrorGroup non marca niente e lo dice (voce D166).
            case 67:
            case 85:
            case 133:
                await HandleGroupStorageErrors(e);
                break;

            default:
                await StepErrorGroup(e.StepGuid, e.ItemGuid, e.ErrorCode, e.ErrorDescription);
                break;
        }
    }

    /// <summary>
    /// Gestisce errori di movimento per dispositivi singoli, routing verso
    /// gestori specifici basati sul codice di errore.
    /// </summary>
    /// <param name="e">Gli argomenti dell'evento di movimento completato con errore</param>
    private async Task ManageMoveToCompleteError(AsiMovementIsCompletedEventArgs? e)
    {
        var isToExecuteStep = false;

        if (e != null)
            switch (e.ErrorCode)
            {
                case 85:
                case 133:
                case 67:
                    isToExecuteStep = await HandleStorageErrors(e);
                    break;

                case 35:
                    await StepError(e.StepGuid, e.ItemGuid, e.ErrorCode, e.ErrorDescription);
                    break;

                default:
                    await HandleDefaultError(e);
                    break;
            }

        if (isToExecuteStep)
            _ = device.ExecuteStep();
    }

    /// <summary>
    /// Processa la logica specifica per tipo di dispositivo dopo il successo
    /// dell'invio di dati LU, gestendo la costruzione di rotte se necessario.
    /// </summary>
    private void ProcessDeviceTypeSpecificLogic()
    {
        switch (device.DeviceType)
        {
            case DeviceTypes.Automation:
            case DeviceTypes.Loading:
            case DeviceTypes.Buffer:
            case DeviceTypes.LoadingAndBuffer:
                if (device.BelongedGroup == null &&
                    (device.DestinationItem != null || device.DestinationItem2 != null ||
                     device.LookWhereAllocateLu != LookWhereAllocateLuTypes.No))
                {
                    // Una lettura sola della missione a bordo, e lo sguardo alla coda insieme
                    // all'accodamento: AutomationDevice.RequeueLoadedLu (voce 41, compito 7 e
                    // revisione finale).
                    device.RequeueLoadedLu();
                }
                break;
        }
    }

    /// <summary>
    /// Processa i dati LU ricevuti dall'ASI, validando codici LU e gestendo
    /// controlli di duplicazione nel magazzino.
    /// </summary>
    /// <param name="e">Gli argomenti dell'evento di invio dati LU</param>
    /// <returns>True se la LU deve essere rifiutata, false altrimenti</returns>
    private async Task<bool> ProcessLuData(AsiSendLuDataEventArgs e)
    {
        if (device.LoadedLu != null)
        {
            var handledLu = Collections.ManagedLu.Instance[device.LoadedLu.MissionId];

            if (string.IsNullOrEmpty(device.LoadedLu.LuCode))
            {
                if (e.LuDataFromAsiResult != null && !string.IsNullOrWhiteSpace(e.LuDataFromAsiResult.LuCode) &&
                    e.LuDataFromAsiResult.LuCode.Contains(";"))
                {
                    var luBarcodes = e.LuDataFromAsiResult.LuCode.Trim().Split(';');
                    device.LoadedLu.LuCode = luBarcodes.FirstOrDefault(s =>
                        s.StartsWith("U", StringComparison.CurrentCultureIgnoreCase));

                    if (string.IsNullOrWhiteSpace(device.LoadedLu.LuCode))
                    {
                        device.LoadedLu.Status = MissionStatusTypes.Error;
                        device.LoadedLu.StatusDescription =
                            $"LU_CODE {e.LuDataFromAsiResult.LuCode}: Wrong PLC LU_CODE acquired";
                        return true;
                    }

                    if (handledLu != null)
                        handledLu.LuCode = device.LoadedLu.LuCode;
                }

                if (await Navigator.RequireWarehouse($"ProcessLuData sull'unita' di carico {device.LoadedLu.LuCode}").IsLoadUnitStoredAsync(device.LoadedLu.LuCode))
                {
                    device.LoadedLu.Status = MissionStatusTypes.Error;
                    device.LoadedLu.StatusDescription =
                        $"LU Code {device.LoadedLu.LuCode} has been already present in the warehouse";
                    device.LoadedLu.LuId = 0;
                    return true;
                }
            }
        }

        if (!string.IsNullOrEmpty(device.LoadedLu?.LuCode)) return false;

        if (device.LoadedLu == null) return true;

        device.LoadedLu.Status = MissionStatusTypes.Error;
        device.LoadedLu.StatusDescription = "Lu code is not present: Wrong PLC LU_CODE acquired";

        return true;

    }

    /// <summary>
    /// Gestisce errori di step singoli, aggiornando stati di missione,
    /// registrando log e aggiornando comandi appropriati.
    /// </summary>
    /// <param name="idStepError">L'identificativo univoco dello step con errore</param>
    /// <param name="idItemCommand">L'identificativo del comando dell'elemento</param>
    /// <param name="errorCode">Il codice di errore</param>
    /// <param name="errorDescription">La descrizione dell'errore</param>
    private async Task StepError(Guid idStepError, Guid idItemCommand, int errorCode, string errorDescription)
    {
        var stepError = Collections.MissionSteps.Instance.FirstOrDefault(x => x.StepId == idStepError);

        if (stepError == null) return;

        await StepError(stepError, idItemCommand, errorCode, errorDescription);
    }

    /// <summary>
    /// Come sopra, per uno step gia' in mano: quello del deposito rifiutato, preso prima della ricerca
    /// di una cella nuova, che puo' toglierlo dagli step (<see cref="HandleStorageErrors"/>).
    /// </summary>
    private async Task StepError(MissionStep stepError, Guid idItemCommand, int errorCode, string errorDescription)
    {
        stepError.EndStep = DateTime.Now;
        stepError.StepStatus = ResultCodeTypes.Error;

        // Il codice e' decimale: la «D» e' solo il prefisso del nome di risorsa nel .resx, non una
        // cifra esadecimale. Letto come esadecimale, un codice a nove cifre andava in overflow qui,
        // fuori dal try: lo step non veniva marcato in errore e la destinazione restava prenotata.
        errorDescription = string.IsNullOrEmpty(errorDescription)
            ? $"Code {errorCode}"
            : $"Code {errorCode} - {errorDescription}";

        try
        {
            if (stepError.PathMission == null || stepError.SourceItem == null ||
                stepError.DestinationItem == null)
                return;

            var fromAsiItem = Navigator.GetDataAsi(stepError.SourceItem);
            var toAsiItem = Navigator.GetDataAsi(stepError.DestinationItem);

            errorDescription = BuildErrorDescription(stepError, fromAsiItem, toAsiItem, errorDescription);

            stepError.PathMission.MovingDescription = errorDescription;
            stepError.PathMission.Status = MissionStatusTypes.Error;

            Navigator.RecordNavigatorLog(device, errorDescription, LogEntryType.Error, stepError);

            device.ForgetTheCellConfirmationsOnBoard([stepError.PathMission]);

            await UpdateCommand(stepError.PathMission, idItemCommand, errorDescription);

            device.Status = DeviceStatusTypes.Warning;

            // Il mittente e' in Warning: la destinazione torna disponibile, altrimenti resta
            // occupata per un movimento che non arrivera' e nessun altro puo' consegnarle nulla.
            stepError.DestinationItem.ReleaseAfterFailedStep(stepError.StepId);

            if (device.BelongedGroup != null)
                device.BelongedGroup.Status = DeviceStatusTypes.Warning;
        }
        catch (Exception ex)
        {
            device.LogError(ex);
        }
    }

    /// <summary>
    /// Gestisce errori di step per dispositivi raggruppati, processando
    /// tutti gli step del gruppo e aggiornando stati appropriati.
    /// </summary>
    /// <param name="groupedMovementIdError">L'identificativo del movimento raggruppato con errore</param>
    /// <param name="idItemCommand">L'identificativo del comando dell'elemento</param>
    /// <param name="errorCode">Il codice di errore</param>
    /// <param name="errorDescription">La descrizione dell'errore</param>
    private async Task StepErrorGroup(Guid groupedMovementIdError, Guid idItemCommand, int errorCode, string errorDescription)
    {
        var errors = StepsOfGroup(groupedMovementIdError, nameof(StepErrorGroup));

        if (errors is not { Count: > 0 }) return;

        await StepErrorGroup(errors, idItemCommand, errorCode, errorDescription);
    }

    /// <summary>
    /// Come sopra, per gli step del movimento di gruppo gia' in mano: quelli del deposito rifiutato,
    /// presi prima della ricerca di una cella nuova, che li toglie dagli step (<see cref="HandleGroupStorageErrors"/>).
    /// </summary>
    private async Task StepErrorGroup(List<MissionStep> errors, Guid idItemCommand, int errorCode, string errorDescription)
    {
        // Il codice e' decimale: la «D» e' solo il prefisso del nome di risorsa nel .resx, non una
        // cifra esadecimale. Letto come esadecimale, un codice a nove cifre andava in overflow qui,
        // fuori dal try: lo step non veniva marcato in errore e la destinazione restava prenotata.
        errorDescription = string.IsNullOrEmpty(errorDescription)
            ? $"Code {errorCode}"
            : $"Code {errorCode} - {errorDescription}";

        var itemsToExecuteStep = new List<AutomationDevice>();

        try
        {
            foreach (var stepError in errors)
                if (stepError.DestinationItem != null)
                    stepError.DestinationItem.ReleaseAfterFailedStep(stepError.StepId);

            foreach (var stepError in errors)
            {
                stepError.EndStep = DateTime.Now;
                stepError.StepStatus = ResultCodeTypes.Error;

                var transferringLu = stepError.PathMission;
                if (transferringLu == null || stepError.SourceItem == null || stepError.DestinationItem == null)
                    continue;

                var sourceItem = stepError.SourceItem;
                var destinationItem = stepError.DestinationItem;

                var fromAsiItem = Navigator.GetDataAsi(sourceItem);
                var toAsiItem = Navigator.GetDataAsi(destinationItem);

                var currentErrorDescription = BuildGroupErrorDescription(
                    transferringLu.LuCode, sourceItem, destinationItem,
                    fromAsiItem, toAsiItem, errorDescription, itemsToExecuteStep);

                if (stepError.PathMission != null)
                {
                    stepError.PathMission.MovingDescription = currentErrorDescription;
                    stepError.PathMission.Status = MissionStatusTypes.Error;
                }

                Navigator.RecordNavigatorLog(device, currentErrorDescription, LogEntryType.Error, stepError);
            }
        }
        catch (Exception ex)
        {
            device.LogError(ex);
        }

        device.ForgetTheCellConfirmationsOnBoard(errors.Select(step => step.PathMission));

        foreach (var item in itemsToExecuteStep)
        {
            item.Status = DeviceStatusTypes.Warning;
            if (item.AutoExecuteStep)
                _ = item.ExecuteStep();
        }

        await CommandToDo.Update(new Command
        {
            CommandType = Command.CommandTypes.ItemExecuteStep,
            Date = DateTime.Now,
            ErrorDescription = errorDescription,
            ItemCommandId = idItemCommand,
            LuId = 0,
            Status = Command.StatusTypes.Error,
            WorkstationId = Navigator.WorkstationId
        });

        device.Status = DeviceStatusTypes.Warning;
    }

    /// <summary>
    /// Gestisce timeout di step singoli, aggiornando stati di missione
    /// e gestendo la logica specifica per tipo di errore di timeout.
    /// </summary>
    /// <param name="idStepError">L'identificativo univoco dello step con timeout</param>
    /// <param name="idItemCommand">L'identificativo del comando dell'elemento</param>
    /// <param name="resultStatus">Il tipo di risultato (timeout, errore di connessione, ecc.)</param>
    private async Task StepTimeOut(Guid idStepError, Guid idItemCommand, ResultCodeTypes resultStatus)
    {
        var stepError = Collections.MissionSteps.Instance.FirstOrDefault(x => x.StepId == idStepError);

        if (stepError == null) return;

        stepError.EndStep = DateTime.Now;
        stepError.StepStatus = resultStatus;

        var errorType = resultStatus switch
        {
            ResultCodeTypes.TimeOut => "Time Out",
            ResultCodeTypes.ConnectionError => "Connection",
            _ => "Unknown"
        };

        try
        {
            if (stepError.PathMission == null || stepError.SourceItem == null ||
                stepError.DestinationItem == null)
                return;

            var fromAsiItem = Navigator.GetDataAsi(stepError.SourceItem);
            var toAsiItem = Navigator.GetDataAsi(stepError.DestinationItem);

            var errorDescription = BuildTimeoutErrorDescription(stepError, fromAsiItem, toAsiItem, errorType);

            stepError.PathMission.MovingDescription = errorDescription;
            stepError.PathMission.Status = MissionStatusTypes.Error;

            Navigator.RecordNavigatorLog(device, errorDescription, LogEntryType.Warning, stepError);

            await UpdateCommand(stepError.PathMission, idItemCommand, errorDescription);

            // Uno step andato in timeout non e' uno step in volo: il campo ha detto che non ce
            // l'ha fatta. Chiuderlo qui e' anche cio' che permette al ritentativo di passare la
            // guardia di ExecuteStep, che rifiuta i dispositivi impegnati.
            device.RunningStepId = Guid.Empty;

            device.Status = DeviceStatusTypes.Warning;

            // Il comando non e' passato e la destinazione non ha ricevuto niente: il posto che
            // il mittente vi aveva preso va restituito. Senza questo, il timeout lascerebbe la
            // destinazione impegnata per uno step concluso male, e nessun altro potrebbe piu'
            // consegnarle nulla finche' l'operatore non azzera l'errore. Il ritentativo non ne
            // soffre: riprende il posto con lo stesso step. Voce D73.
            // Stato e posto insieme: prima si restituiva solo il posto, e la destinazione restava
            // Busy per un movimento gia' concluso male.
            stepError.DestinationItem.ReleaseAfterFailedStep(stepError.StepId);

            Navigator.WriteLog(
                $"StepTimeOut: liberata la destinazione {stepError.DestinationItem.ItemGuid} dello step {stepError.StepId}",
                LogEntryType.Information);

            UpdateBelongedGroupStatus(stepError);
        }
        catch (Exception ex)
        {
            device.LogError(ex);
        }
    }

    /// <summary>
    /// Gestisce timeout di step per dispositivi raggruppati, processando
    /// tutti gli step del gruppo e aggiornando stati appropriati.
    /// </summary>
    /// <param name="groupedMovementIdError">L'identificativo del movimento raggruppato con timeout</param>
    /// <param name="idItemCommand">L'identificativo del comando dell'elemento</param>
    /// <param name="resultStatus">Il tipo di risultato (timeout, errore di connessione, ecc.)</param>
    private async Task StepTimeOutGroup(Guid groupedMovementIdError, Guid idItemCommand, ResultCodeTypes resultStatus)
    {
        var stepErrors = StepsOfGroup(groupedMovementIdError, nameof(StepTimeOutGroup));
        if (stepErrors is not { Count: > 0 }) return;

        var itemsToExecuteStep = new List<AutomationDevice>();

        try
        {
            foreach (var stepError in stepErrors.Where(s => s.DestinationItem != null))
                if (stepError.DestinationItem != null)
                    stepError.DestinationItem.ReleaseAfterFailedStep(stepError.StepId);

            var errorType = resultStatus switch
            {
                ResultCodeTypes.TimeOut => "Time Out",
                ResultCodeTypes.ConnectionError => "Connection",
                _ => "Unknown"
            };

            var errorDescription = string.Empty;

            foreach (var stepError in stepErrors)
            {
                stepError.EndStep = DateTime.Now;
                stepError.StepStatus = resultStatus;

                if (stepError.SourceItem == null || stepError.DestinationItem == null ||
                    stepError.PathMission == null) continue;

                var fromAsiItem = Navigator.GetDataAsi(stepError.SourceItem);
                var toAsiItem = Navigator.GetDataAsi(stepError.DestinationItem);

                errorDescription = BuildGroupTimeoutErrorDescription(
                    stepError, fromAsiItem, toAsiItem, errorType, itemsToExecuteStep);

                stepError.PathMission.MovingDescription = errorDescription;
                stepError.PathMission.Status = MissionStatusTypes.Error;

                Navigator.RecordNavigatorLog(device, errorDescription, LogEntryType.Error, stepError);
            }

            foreach (var item in itemsToExecuteStep)
            {
                // Prima dello stato: lo step di questi mittenti e' andato in timeout, non e'
                // piu' in volo, e senza chiuderlo la riesecuzione qui sotto verrebbe rifiutata
                // dalla guardia di ExecuteStep.
                item.RunningStepId = Guid.Empty;

                item.Status = DeviceStatusTypes.Warning;
                if (item.AutoExecuteStep)
                    _ = item.ExecuteStep();
            }

            await CommandToDo.Update(new Command
            {
                CommandType = Command.CommandTypes.ItemExecuteStep,
                Date = DateTime.Now,
                ErrorDescription = errorDescription,
                ItemCommandId = idItemCommand,
                LuId = 0,
                Status = Command.StatusTypes.Error,
                WorkstationId = Navigator.WorkstationId
            });

            device.Status = DeviceStatusTypes.Warning;
        }
        catch (Exception ex)
        {
            device.LogError(ex);
        }
    }

    /// <summary>
    /// Aggiorna lo stato del gruppo di appartenenza basato sul risultato dello step,
    /// gestendo la sincronizzazione degli stati tra dispositivi raggruppati.
    /// </summary>
    /// <param name="stepError">Lo step della missione che ha generato l'errore</param>
    private void UpdateBelongedGroupStatus(MissionStep stepError)
    {
        if (stepError.DestinationItem?.BelongedGroup != null)
        {
            stepError.DestinationItem.BelongedGroup.Status =
                stepError.PathMission != null && stepError.DestinationItem.BelongedGroup.RunningStepId == stepError.PathMission.GroupedMovementId
                    ? DeviceStatusTypes.Warning
                    : DeviceStatusTypes.Ready;
        }

        if (stepError.SourceItem?.BelongedGroup != null)
        {
            stepError.SourceItem.BelongedGroup.Status =
                stepError.PathMission != null && stepError.SourceItem.BelongedGroup.RunningStepId == stepError.PathMission.GroupedMovementId
                    ? DeviceStatusTypes.Warning
                    : DeviceStatusTypes.Ready;
        }
    }

    /// <summary>
    /// Aggiorna il comando associato alla missione di percorso con lo stato di errore
    /// e la descrizione appropriata.
    /// </summary>
    /// <param name="pathMission">La missione di percorso da aggiornare</param>
    /// <param name="idItemCommand">L'identificativo del comando dell'elemento</param>
    /// <param name="errorDescription">La descrizione dell'errore</param>
    private static async Task UpdateCommand(PathMission pathMission, Guid idItemCommand, string errorDescription)
    {
        if (pathMission.IdCommandToDo > 0)
        {
            await CommandToDo.SetStatus(pathMission.IdCommandToDo, Command.StatusTypes.Error, errorDescription);
        }
        else
        {
            await CommandToDo.Update(new Command
            {
                CommandType = Command.CommandTypes.ItemExecuteStep,
                Date = DateTime.Now,
                ErrorDescription = errorDescription,
                ItemCommandId = idItemCommand,
                LuId = pathMission.LuId,
                Status = Command.StatusTypes.Error,
                WorkstationId = Navigator.WorkstationId
            });
        }
    }

    /// <summary>
    /// Aggiorna lo stato di esecuzione del gruppo per il dispositivo destinazione,
    /// gestendo la sincronizzazione degli stati di esecuzione tra dispositivi raggruppati.
    /// </summary>
    /// <param name="destinationItem">Il dispositivo destinazione</param>
    /// <param name="pathMission">La missione di percorso associata</param>
    private void UpdateGroupExecutionStatus(AutomationDevice destinationItem, PathMission pathMission)
    {
        if (destinationItem.BelongedGroup != null)
        {
            if (destinationItem.BelongedGroup.RunningStepId != pathMission.GroupedMovementId)
                destinationItem.BelongedGroup.Status = DeviceStatusTypes.Ready;
            destinationItem.BelongedGroup.GroupExecuteStepRunning = false;
        }
    }

    #endregion Private Methods

}
