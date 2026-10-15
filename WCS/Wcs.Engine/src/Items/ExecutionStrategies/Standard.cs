using Wcs.Engine.Collections;
using Wcs.Engine.Interfaces;
using static Wcs.Common.Enums;
using static Wcs.Engine.Items.AutomationDevice;

namespace Wcs.Engine.Items.ExecutionStrategies
{
    /// <summary>
    /// Implementa la strategia di esecuzione standard per i dispositivi di automazione.
    /// Gestisce il flusso di esecuzione delle operazioni di caricamento, scaricamento e trasferimento delle unità di carico (LU).
    /// </summary>
    /// <param name="device">Il dispositivo di automazione associato a questa strategia di esecuzione</param>
    internal class Standard(AutomationDevice device) : IExecutionStrategy
    {

        #region Private Fields

        /// <summary>
        /// Flag che indica se costruire un percorso quando non ci sono step da eseguire
        /// </summary>
        private bool _buildRouteOnNoStepToDo;

        #endregion Private Fields

        #region Public Methods

        /// <summary>
        /// Esegue la strategia di automazione standard, gestendo il ciclo completo di operazioni del dispositivo.
        /// Controlla le LU da ricevere, gestisce le celle vuote, processa le destinazioni e avvia le operazioni di trasferimento.
        /// </summary>
        /// <param name="cancellationToken">Token per la cancellazione dell'operazione asincrona</param>
        /// <returns>Task che rappresenta l'operazione asincrona</returns>
        /// <exception cref="OperationCanceledException">Sollevata se il token viene cancellato.</exception>
        public async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            // Il token e' quello di dismissione del dispositivo: si controlla all'ingresso e
            // prima del deposito, cioe' fra due passi e non a meta' di uno. Un dispositivo che
            // sta chiudendo non deve avviare un nuovo movimento.
            cancellationToken.ThrowIfCancellationRequested();

            device.LuManager?.CheckLuToReceive();

            // La missione caricata e la versione della sua rotta, lette prima di guardarne lo step da
            // fare: la rotta si toglie solo se e' ancora quella su cui si e' visto «nessuno step da
            // fare» (voce 41, corsa 6; vedi sotto).
            var observed = device.LoadedLu;
            var observedRouteVersion = observed?.RouteVersion ?? 0;

            if (device.LUsLoaded.Count == 0 || device.LoadedLu == null)
            {
                _buildRouteOnNoStepToDo = false;

                if (!string.IsNullOrEmpty(device.EmptyCellSourceDoor) && device.Status == DeviceStatusTypes.Ready)
                    await device.AddMissionToEmptyCell(device.EmptyCellSourceDoor, device.EmptyCellDestinationDoor,
                        device.EmptyCellIdWorkstation, (int)Math.Round(device.EmptyCellIdLuType));

                // Attesa, non «lancia e dimentica». Era l'unico dei quattro punti di chiamata di
                // CheckGetFromItem a non attendere, e la differenza e' fra «lo sappiamo» e «non lo
                // sapremo mai»: il 2026-09-09 l'eccezione della lettura verso il gestionale fini'
                // in un Task che nessuno osservava, .NET la scarto' in silenzio, e il registro non
                // disse niente per un'ora e mezza. Ora passa dal catch di ExecuteStep, che la
                // scrive.
                //
                // Il tempo dentro il semaforo di esecuzione si allunga, ed e' voluto. Non e' un
                // massimo nuovo: il ramo gemello di questo stesso metodo - il deposito, da
                // CheckPutToItem a StartStoreLu - e' gia' atteso qui dentro e contiene le stesse
                // letture e la stessa Task.Delay(200). E il dispositivo, in quella finestra,
                // rifiutava gia': il prelievo staccato prendeva RunningStepId pochi istanti dopo, e
                // da li' in poi IsBusyWithCommand rifiutava chiunque fino alla risposta del campo.
                // Cio' che l'attesa copre in piu' e' proprio l'istante che prima restava scoperto,
                // in cui una seconda esecuzione poteva entrare in StartPickUpLu per la stessa
                // LUtoPick - la prenotazione dello stesso step riesce due volte - e mandare al
                // campo due comandi per un movimento solo.
                //
                // Chi viene rifiutato non si perde: il rifiuto non riaccoda (e' la regola scritta
                // su ExecuteStep), ma il ciclo delle estrazioni richiama ExecuteStep ogni due
                // secondi e i vicini richiamano chi si e' iscritto in DevicesReadyToTransferLu.
                if (device.LuManager != null) await device.LuManager.CheckGetFromItem();
            }
            else if (device.LoadedLu?.DestinationItem == device)
            {
                _buildRouteOnNoStepToDo = false;

                device.LoadedLu.Status = MissionStatusTypes.Done;
                device.Status = DeviceStatusTypes.Ready;

                if (device is
                    {
                        LookWhereAllocateLu: LookWhereAllocateLuTypes.No, DestinationItem: null, DestinationItem2: null
                    })
                    return;

                await AddLoadedLuToRouteToBuildQueue();
            }
            else if (device.LoadedLu?.StepToDo == null)
            {
                if (_buildRouteOnNoStepToDo)
                {
                    _buildRouteOnNoStepToDo = false;
                    return;
                }

                if (device is
                    {
                        LookWhereAllocateLu: LookWhereAllocateLuTypes.No, DestinationItem: null, DestinationItem2: null
                    } && (device.LoadedLu is not { DestinationItem: not null } ||
                          device.LoadedLu.DestinationItem == device))
                    return;

                _buildRouteOnNoStepToDo = true;
                device.NoStepToDoPauseForTesting?.Invoke();

                // Lo step da fare e' nullo anche mentre un costruttore sta fra la sua rimozione e la
                // sua scrittura: la rotta si toglie solo se e' ancora quella vista qui sopra
                // (PathMission.RemoveTheRouteSeenWithoutAStepToDo). Se la missione caricata non e'
                // piu' quella, non si tocca niente e si va avanti come con uno step da fare (voce 41,
                // corsa 6: prima la rotta appena scritta si toglieva, e la missione rifaceva un giro di
                // coda).
                //
                // Se la versione e' cambiata (perso il confronto) la rotta resta com'e'. Con una rotta,
                // l'ha scritta un altro, e si va avanti come con uno step da fare, senza rimettere la
                // missione in coda. Senza rotta la versione e' salita per altro: una rimozione, una
                // destinazione cambiata senza rotta dopo, un costruttore che ha tolto e non ha trovato
                // percorso. Allora la missione torna al giro delle rotte, come chi perde il confronto
                // (decisione 1 del 10/10): prima restava senza rotta e fuori dalla coda fino al
                // prossimo ExecuteStep di questo dispositivo (revisione finale della voce 41).
                if (device.LoadedLu is { } loaded && !ReferenceEquals(loaded, observed))
                    _buildRouteOnNoStepToDo = false;
                else if (device.LoadedLu is { } seen && !seen.RemoveTheRouteSeenWithoutAStepToDo(observedRouteVersion) &&
                         seen.MissionSteps.Count > 0)
                    _buildRouteOnNoStepToDo = false;
                else
                    await AddLoadedLuToRouteToBuildQueue();
            }
            else
            {
                _buildRouteOnNoStepToDo = false;
            }

            cancellationToken.ThrowIfCancellationRequested();

            await CheckPutToItem();
        }

        #endregion Public Methods

        #region Private Methods

        /// <summary>
        /// Determina se uno step di destinazione deve essere eseguito in base alle condizioni del dispositivo di destinazione.
        /// </summary>
        /// <param name="destinationItem">Il dispositivo di destinazione da verificare</param>
        /// <returns>True se lo step deve essere eseguito, false altrimenti</returns>
        private static bool ShouldExecuteDestinationStep(AutomationDevice destinationItem)
        {
            return destinationItem is not { IsLinkedToStorage: true, BelongedGroup: null } &&
                   destinationItem.IsReadyToReceiveLu;
        }

        /// <summary>
        /// Aggiunge un dispositivo di destinazione alla coda di trasferimento, gestendo eventuali incroci e determinando l'elemento target.
        /// </summary>
        /// <param name="destinationItem">Il dispositivo di destinazione da aggiungere alla coda</param>
        private void AddDestinationItemToTransferQueue(AutomationDevice destinationItem)
        {
            var crossRoadItem = destinationItem.LuManager?.CheckCrossroad(device.LoadedLu);

            if (crossRoadItem != null)
            {
                device.StepExecutor?.AddDevicesReadyToTransferLu(crossRoadItem);
            }
            else
            {
                var targetItem = GetTargetItemForTransfer(destinationItem);
                device.StepExecutor?.AddDevicesReadyToTransferLu(targetItem);
            }
        }

        /// <summary>
        /// Aggiunge la LU caricata alla coda di costruzione percorsi se non è già presente.
        /// </summary>
        /// <returns>Task che rappresenta l'operazione asincrona</returns>
        private async Task AddLoadedLuToRouteToBuildQueue()
        {
            // Una lettura sola della missione a bordo, e lo sguardo alla coda insieme all'accodamento:
            // AutomationDevice.RequeueLoadedLu (voce 41, compito 7 e revisione finale).
            device.RequeueLoadedLu();

            await Task.CompletedTask;
        }

        /// <summary>
        /// Verifica se è possibile avviare un'operazione di deposito (put-to) verso il dispositivo di destinazione specificato.
        /// </summary>
        /// <param name="destinationItem">Il dispositivo di destinazione per l'operazione di deposito</param>
        /// <returns>True se l'operazione può essere avviata, false altrimenti</returns>
        private bool CanStartPutToOperation(AutomationDevice destinationItem)
        {
            if (destinationItem.IsLinkedToStorage)
                return false;

            return (device.IsReadyToTransferLu &&
                    destinationItem.Status != DeviceStatusTypes.OffLine &&
                    destinationItem.LuManager?.CanAcceptLu(device.LoadedLu) == true) ||
                   (device.IsReadyToTransferLu &&
                    destinationItem is { IsReadyToReceiveLu: true, LuManager: not null } &&
                    destinationItem.LuManager.CanAcceptLu(device.LoadedLu));
        }

        /// <summary>
        /// Controlla e gestisce le operazioni di deposito verso altri dispositivi in base al tipo di dispositivo di destinazione.
        /// </summary>
        /// <returns>Task che rappresenta l'operazione asincrona</returns>
        private async Task CheckPutToItem()
        {
            if (!ShouldProcessDestinationItem(out var destinationItem))
                return;

            if (destinationItem == null) return;

            switch (destinationItem.DeviceType)
            {
                case DeviceTypes.Automation:
                case DeviceTypes.Loading:
                case DeviceTypes.Buffer:
                case DeviceTypes.CircularDistributor:
                case DeviceTypes.LoadingAndBuffer:
                    await HandleAutomationDeviceTypes(destinationItem);
                    break;

                case DeviceTypes.Storage:
                    // StorageManager e' costruito da Initialize e non torna mai nullo: la
                    // guardia sostituisce "await x?.M()", che sollevava NullReferenceException.
                    if (device.StorageManager != null) await device.StorageManager.StartStoreLu();
                    break;

                case DeviceTypes.GroupOfAutomationDevices:
                case DeviceTypes.None:
                default:
                    break;
            }

        }

        /// <summary>
        /// Determina l'elemento target per il trasferimento, considerando le condizioni di disponibilità della destinazione.
        /// </summary>
        /// <param name="destinationItem">Il dispositivo di destinazione originale</param>
        /// <returns>Il dispositivo target per il trasferimento, che può essere diverso da quello originale</returns>
        private AutomationDevice? GetTargetItemForTransfer(AutomationDevice destinationItem)
        {
            if (destinationItem is { AcceptLuIfDestinationIsAvailable: true, LuManager: not null } &&
                destinationItem.LuManager.StepIsAvailable(device.LoadedLu) == StepAvailableStatusTypes.DestinationNotAvailable)
            {
                return destinationItem.StepToDoSource(device.LoadedLu)?.DestinationItem;
            }

            return destinationItem;
        }

        /// <summary>
        /// Gestisce le operazioni per i tipi di dispositivi di automazione in base al tipo di movimento configurato.
        /// </summary>
        /// <param name="destinationItem">Il dispositivo di automazione di destinazione</param>
        /// <returns>Task che rappresenta l'operazione asincrona</returns>
        private async Task HandleAutomationDeviceTypes(AutomationDevice destinationItem)
        {
            if (device.DeviceType == DeviceTypes.Loading)
                destinationItem.LUtoReceive = null;

            switch (destinationItem.MotionType)
            {
                case MotionTypes.MoveTo:
                    await HandleMoveToMotion(destinationItem);
                    break;

                case MotionTypes.GetFrom:
                    HandleGetFromMotion(destinationItem);
                    break;
            }
        }

        /// <summary>
        /// Gestisce il movimento di tipo GetFrom, configurando i dispositivi per il trasferimento e l'esecuzione degli step.
        /// </summary>
        /// <param name="destinationItem">Il dispositivo di destinazione per l'operazione GetFrom</param>
        private void HandleGetFromMotion(AutomationDevice destinationItem)
        {
            device.StepExecutor?.AddDevicesReadyToTransferLu(destinationItem);
            device.Status = DeviceStatusTypes.Ready;
            device.RunningStepId = Guid.Empty;

            if (ShouldSetDestinationItemReady(destinationItem))
            {
                destinationItem.Status = DeviceStatusTypes.Ready;
            }

            if (ShouldExecuteDestinationStep(destinationItem))
            {
                _ = destinationItem.ExecuteStep();
            }
        }

        /// <summary>
        /// Gestisce il movimento di tipo MoveTo, avviando l'operazione di deposito o gestendo i blocchi.
        /// </summary>
        /// <param name="destinationItem">Il dispositivo di destinazione per l'operazione MoveTo</param>
        /// <returns>Task che rappresenta l'operazione asincrona</returns>
        private async Task HandleMoveToMotion(AutomationDevice destinationItem)
        {
            if (CanStartPutToOperation(destinationItem))
            {
                device.StartPutToMOVE_LU();
            }
            else
            {
                await HandlePutToOperationBlocked(destinationItem);
            }
        }

        /// <summary>
        /// Gestisce i casi in cui l'operazione di deposito è bloccata, aggiornando gli stati e gestendo la coda di trasferimento.
        /// </summary>
        /// <param name="destinationItem">Il dispositivo di destinazione per cui l'operazione è bloccata</param>
        /// <returns>Task che rappresenta l'operazione asincrona</returns>
        private async Task HandlePutToOperationBlocked(AutomationDevice destinationItem)
        {
            if (device.LoadedLu != null) device.LoadedLu.Status = MissionStatusTypes.Stop;
            device.Status = DeviceStatusTypes.Ready;

            if (ShouldAddDeviceToTransferQueue())
            {
                AddDestinationItemToTransferQueue(destinationItem);
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Determina se il dispositivo deve essere aggiunto alla coda di trasferimento in base alle sue caratteristiche.
        /// </summary>
        /// <returns>True se il dispositivo deve essere aggiunto alla coda, false altrimenti</returns>
        private bool ShouldAddDeviceToTransferQueue()
        {
            // Non aggiungere se il device è collegato allo storage e non appartiene a un gruppo
            if (device is { IsLinkedToStorage: true, BelongedGroup: null } &&
                device.DeviceType != DeviceTypes.GroupOfAutomationDevices)
                return false;

            return !device.IsLinkedToStorage;
        }

        /// <summary>
        /// Verifica se il dispositivo di destinazione deve essere processato e restituisce il dispositivo valido.
        /// </summary>
        /// <param name="destinationItem">Il dispositivo di destinazione trovato, se valido</param>
        /// <returns>True se il dispositivo deve essere processato, false altrimenti</returns>
        private bool ShouldProcessDestinationItem(out AutomationDevice? destinationItem)
        {
            destinationItem = null;

            if (device.LoadedLu?.StepToDo?.DestinationItem == null)
                return false;

            try
            {
                destinationItem = device.LoadedLu.StepToDo.DestinationItem;
            }
            catch (Exception)
            {
                return false;
            }

            return destinationItem is { BelongedGroup: null };
        }

        /// <summary>
        /// Determina se il dispositivo di destinazione deve essere impostato come pronto in base alle condizioni di missione.
        /// </summary>
        /// <param name="destinationItem">Il dispositivo di destinazione da verificare</param>
        /// <returns>True se il dispositivo deve essere impostato come pronto, false altrimenti</returns>
        private bool ShouldSetDestinationItemReady(AutomationDevice destinationItem)
        {
            return destinationItem.RunningStepId == Guid.Empty &&
                   device.LoadedLu != null &&
                   destinationItem.LUtoReceive != null &&
                   device.LoadedLu.MissionId == destinationItem.LUtoReceive.MissionId;
        }

        #endregion Private Methods

    }
}
